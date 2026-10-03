using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DNS-over-HTTPS 解析器
    /// <para>
    /// 明文 DNS（53 端口）在部分网络下会被 RST 注入，导致 github.com 等域名
    /// 完全解析不到 IP、程序退化为只会重试几个过期的持久化 IP。
    /// DoH 走 443 端口的 HTTPS 通道，天然规避明文 DNS 的 RST 干扰。
    /// 这里全部使用 IP 字面量直连，DoH 客户端自身也不再依赖任何会被污染的 DNS。
    /// </para>
    /// <para>
    /// 采用 DNS wire 格式（请求以 base64url 放在 ?dns= 参数），这是所有标准 DoH
    /// 服务器都支持的「通用语」，比各家实现不一的 JSON API 更稳。
    /// </para>
    /// </summary>
    sealed class DohResolver
    {
        /// <summary>
        /// DoH 端点熔断状态的落盘文件名（工作目录下，与 dnsendpoints.json 同级）。
        /// </summary>
        private static readonly string blockedFile = "doh_blocked_endpoints.json";

        /// <summary>
        /// 正查询缓存时长：成功的 DoH 结果会被复用，避免后台每秒一次的测速把 DoH 打爆。
        /// <para>
        /// 取 10 分钟而非更短：GitHub 边缘 IP 本身很稳定，缓存过短会让候选 IP 频繁漂移
        /// （新 IP 不断插到候选池前面、重排），同一域名反复换 IP 反而更易触发对端风控、
        /// 降低"粘性"。需要新 IP 时，健康度失败会触发 RefreshAsync 主动清缓存重解析，
        /// 不依赖这里的短 TTL，因此 10 分钟足够安全。
        /// </para>
        /// </summary>
        private static readonly TimeSpan positiveCacheTtl = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 单次 DoH 请求的整体预算。本机网络到 DoH 端点的 RTT 可能达数秒
        /// （与到 GitHub 的 RTT 同源），8s 曾在拥塞时把"慢但可用"的国内端点判死，
        /// 导致所有端点均不可用。放宽到 12s 留足余量；端点级还有重试兜底。
        /// </summary>
        private static readonly TimeSpan perRequestTimeout = TimeSpan.FromSeconds(12d);

        /// <summary>
        /// 端点熔断：连续失败达到该次数后，暂停使用该端点 <see cref="endpointCooldown"/>。
        /// <para>
        /// 【为什么必须熔断】端点列表是"按序遍历、命中即止"，这在**所有端点都健康**时最优
        /// （只打扰第一个）。但现实中常有部分端点被墙/被限速：实测本机
        /// 223.5.5.5 仅 98~237ms，而 119.29.29.29 与 1.1.1.1 **稳定 10 秒超时**。
        /// 一旦排在前面的端点失手，串行遍历会把后面每个端点的
        /// perRequestTimeout×重试 全部等完（最坏 4 端点 × 12s × 2 次 = 96s），
        /// 单个域名的解析被拖到几十秒，而上层请求的 Timeout 只有 25s —— 直接 504。
        /// 熔断后失效端点会被跳过，只保留真正可达的端点，
        /// 解析耗时回落到"最快可达端点"的量级，且**不增加任何额外请求流量**。
        /// </para>
        /// </summary>
        private const int endpointFailureThreshold = 2;

        /// <summary>
        /// 熔断后的冷却时长：给被墙端点恢复的机会，同时不至于让一次网络抖动就长期弃用端点。
        /// </summary>
        private static readonly TimeSpan endpointCooldown = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 使用 IP 字面量（而非域名）作为 DoH 端点，使客户端无需先解析域名，
        /// 彻底绕开「明文 DNS 被 RST」这一原始故障点。
        /// 证书校验在此处放宽（直连 IP 时 SNI 为 IP，无法匹配服务商证书域名）；
        /// 取回的 IP 会在 IPAddressService 中再做 TCP+TLS 实测，真正连 GitHub 时的
        /// 证书链校验仍由代理层执行，放宽 DoH 一侧不会带来实际安全风险。
        /// </summary>
        private static readonly string[] Endpoints = new[]
        {
            "https://223.5.5.5/dns-query",   // 阿里 DNS，国内几乎必然可达
            "https://119.29.29.29/dns-query", // 腾讯 DNSPod
            "https://1.1.1.1/dns-query",      // Cloudflare
            "https://8.8.8.8/dns-query",      // Google
        };

        /// <summary>
        /// 全局 DoH 在途请求上限。
        /// <para>
        /// 【为什么需要闸门】多端点竞速把"单域名 4 个并发"叠乘到"冷启动同时解析十几个域名"，
        /// 峰值可达 4 端点 × 2 记录类型 × 15 域名 = 120 个并发 HTTPS 请求，
        /// 形态上过于像攻击流量，且会挤占本就紧张的下行带宽。
        /// 这里用信号量把在途请求压在 8 个：既保留"最快端点胜出"的延迟收益
        /// （实测可达端点 RTT 仅 100~240ms，8 路闸门根本不构成瓶颈），
        /// 又让突发规模可控。
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim requestGate = new(DohRequestConcurrency, DohRequestConcurrency);

        /// <summary>
        /// DoH 在途请求并发上限，取 8。
        /// </summary>
        private const int DohRequestConcurrency = 8;

        private readonly ILogger<DohResolver> logger;
        private readonly HttpClient httpClient;
        private readonly ConcurrentDictionary<string, (DateTime Expires, IReadOnlyList<IPAddress> Addresses)> positiveCache = new();

        /// <summary>
        /// 同一 host 的并发解析合流（single-flight）。
        /// <para>
        /// 【v2.6.2 修正】正缓存是"查完才写"，冷启动时几十个请求同时问同一个域名，
        /// 会在缓存写入前全部落空并各自发起一次完整 DoH 查询。
        /// 实测 v2.6.2 日志：api.github.com 在 60 秒内被解析 15 次（另有 github.com 4 次），
        /// 这些重复查询既白耗跨境带宽，也让 DoH 端点更早触发风控。
        /// 用 inflight 字典让后来者直接复用首个请求的 Task，查询次数从 N 降为 1。
        /// </para>
        /// </summary>
        private readonly ConcurrentDictionary<string, Task<IReadOnlyList<IPAddress>>> inflight = new();

        /// <summary>
        /// 各 DoH 端点的熔断状态：值为 (连续失败次数, 熔断至何时)。
        /// </summary>
        private readonly ConcurrentDictionary<string, (int Failures, DateTime BlockedUntil)> endpointHealth = new();

        /// <summary>
        /// DoH 解析器
        /// </summary>
        public DohResolver(ILogger<DohResolver> logger)
        {
            this.logger = logger;
            this.httpClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            })
            {
                Timeout = perRequestTimeout
            };

            // 【跨进程继承熔断状态】程序往往连续运行数小时甚至跨重启，
            // 而"哪些 DoH 端点被墙"是**网络环境属性**，不会因为重启而改变。
            // 若每次启动都从零开始试探，Startup 的测速必然先被死端点吃掉几十秒，
            // 这正是"刚启动那几分钟大量 504"的直接来源。
            // 因此把熔断状态落盘（很小的文件），重启后直接跳过已知死端点。
            try
            {
                if (File.Exists(blockedFile))
                {
                    var blocked = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(blockedFile)) ?? new List<string>();
                    var until = DateTime.UtcNow.Add(endpointCooldown);
                    foreach (var endpoint in blocked)
                    {
                        if (Endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
                        {
                            this.endpointHealth[endpoint] = (Failures: endpointFailureThreshold, BlockedUntil: until);
                        }
                    }

                    if (blocked.Count > 0)
                    {
                        this.logger.LogWarning($"已从上次运行继承 {blocked.Count} 个不可用 DoH 端点的熔断状态（冷却 {endpointCooldown.TotalMinutes:F0} 分钟）：{string.Join(", ", blocked)}");
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug($"读取 DoH 端点熔断状态失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 把当前处于熔断期的端点写入磁盘，供下次启动继承。
        /// </summary>
        private void PersistEndpointHealth()
        {
            try
            {
                var now = DateTime.UtcNow;
                var blocked = this.endpointHealth
                    .Where(item => item.Value.BlockedUntil > now)
                    .Select(item => item.Key)
                    .ToArray();

                if (blocked.Length > 0)
                {
                    File.WriteAllText(blockedFile, JsonSerializer.Serialize(blocked));
                }
                else if (File.Exists(blockedFile))
                {
                    File.Delete(blockedFile);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogDebug($"保存 DoH 端点熔断状态失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 使正查询缓存立即失效。
        /// <para>
        /// 由「IP 被阻断后的恢复」路径调用：正缓存有 10 分钟，若不失效，
        /// 失败后触发的重解析会原样拿回同一批刚被阻断的 IP，恢复速度永远快不起来。
        /// 只在明确的故障恢复时调用，正常轮询期间不动缓存（避免 IP 频繁漂移）。
        /// </para>
        /// </summary>
        public void InvalidateCache()
        {
            this.positiveCache.Clear();
            this.inflight.Clear();
        }

        /// <summary>
        /// 通过 DoH 解析域名的 A / AAAA 记录
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="cancellationToken"></param>
        /// <returns>去重并过滤后的 IP 列表（失败时返回空列表，绝不抛异常）</returns>
        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (this.positiveCache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                return cached.Addresses;
            }

            // 【single-flight】同一 host 的并发查询合流为一次。
            // 注意用 Task.Run 包装成"冷"任务再入字典：若直接用本协程的 Task，
            // await 尚未完成时字典还没写入，并发者仍会各自发起查询（合流失效）。
            var task = this.inflight.GetOrAdd(host, _ => Task.Run(
                async () => await this.ResolveCoreAsync(host, cancellationToken),
                CancellationToken.None));

            try
            {
                return await task;
            }
            finally
            {
                // 只移除"自己登记的"那一个，避免误删后来者的新任务
                this.inflight.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<IPAddress>>>(host, task));
            }
        }

        private async Task<IReadOnlyList<IPAddress>> ResolveCoreAsync(string host, CancellationToken cancellationToken)
        {
            // A 与 AAAA 并行查询，合并结果
            var aTask = ResolveWireAsync(host, type: 1, cancellationToken);
            var aaaaTask = ResolveWireAsync(host, type: 28, cancellationToken);
            await Task.WhenAll(aTask, aaaaTask);

            var merged = new HashSet<IPAddress>();
            foreach (var addresses in new[] { aTask.Result, aaaaTask.Result })
            {
                foreach (var address in addresses)
                {
                    merged.Add(address);
                }
            }

            var list = merged.ToArray();
            var succeeded = (aTask.Result.Count + aaaaTask.Result.Count) > 0 ? 1 : 0;
            if (list.Length > 0)
            {
                this.positiveCache[host] = (DateTime.UtcNow.Add(positiveCacheTtl), list);
                this.logger.LogInformation($"DoH 解析 {host} 成功，得 {list.Length} 个候选IP");
            }
            else
            {
                this.logger.LogWarning($"DoH 解析 {host} 失败：所有端点均不可用");
            }

            return list;
        }

        /// <summary>
        /// 解析域名的 A / AAAA 记录
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> ResolveWireAsync(string host, ushort type, CancellationToken cancellationToken)
        {
            var requestBytes = BuildRequest(host, type);
            if (requestBytes.Length == 0)
            {
                // 域名格式非法（如连续点、标签超长），不发无意义的请求
                return Array.Empty<IPAddress>();
            }

            var dnsParam = ToBase64Url(requestBytes);

            // 【v2.6.4 关键修复】由"串行遍历 + 命中即止"改为**多端点并发竞速**。
            //
            // 【为什么必须改】旧实现按"国内优先(阿里/腾讯) -> 国际兜底(Cloudflare/Google)"
            // 串行遍历。实测本机 223.5.5.5 约 100~240ms 可达，但**首次启动时它常常并不快**
            // （TLS 握手要现建、端点侧可能正在排队），于是一个域名要等满
            // perRequestTimeout(12s) × 重试(2) 才轮到下一个端点。
            // 冷启动时三个端点各等一轮 = 最坏 12×2 + 12×2 + 12×2 = 72s，
            // 实际日志观测到 github.com 从发起（22:01:51）到解析成功（22:02:21）耗时 **30 秒**，
            // 而交互域名的整体请求预算只有 25s —— 直接 504。
            //
            // 【为什么并发是对的】所有标准 DoH 服务器对**同一域名、同一记录类型**
            // 返回的是同一份权威答案（都来自权威 NS），只是延迟不同。
            // 因此"最快可用者"与"逐个尝试"得到的答案完全一致，
            // 而耗时从"最慢端点的累加"降为"最快端点的 RTT"。
            // 实测本机可达端点的 RTT 是 100~240ms —— 并发后单域名首查应落在这个量级。
            //
            // 【流量代价可控】同一时刻最多 4 个并发请求，且：
            //   1) 熔断机制继续生效：连续失败的端点会被跳过，跨境死端点很快不再参与；
            //   2) 竞速是"先回者胜"，其余请求在首个成功后立即被取消（见 winnerCts），
            //      正常情况下不会打满 4 个请求。
            // 相比"几十个域名串行各等 12~24s"，总流量与耗时都是净减少。
            var merged = new HashSet<IPAddress>();
            var available = new List<string>();
            foreach (var endpoint in Endpoints)
            {
                if (IsEndpointBlocked(endpoint) == false)
                {
                    available.Add(endpoint);
                }
            }

            if (available.Count == 0)
            {
                // 全部端点都在熔断冷却期：这是"网络环境已彻底不可用"，
                // 此时再发起请求也只会白等 perRequestTimeout，直接返回空。
                this.logger.LogWarning("所有 DoH 端点均处于熔断冷却期，本轮不发起查询");
                return merged.ToArray();
            }

            // winnerCts 在首个端点成功返回后立即取消，其余在途请求随之作废。
            // 注意它与 cancellationToken 链接：外部停机时它同样会被取消。
            using var winnerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tasks = available
                .Select(endpoint => QueryEndpointRaceAsync(endpoint, dnsParam, winnerCts.Token))
                .ToArray();

            // 逐个收割：任何一个端点带回结果即认定成功并取消其余。
            // 用 WhenAny 而非 WhenAll，是为了把耗时从"最慢端点"降到"最快端点"。
            var pending = new HashSet<Task<IReadOnlyList<IPAddress>>>(tasks);
            var anySucceeded = false;
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);

                var addresses = await finished;
                foreach (var address in addresses)
                {
                    merged.Add(address);
                }

                if (merged.Count > 0)
                {
                    anySucceeded = true;
                    break;
                }
            }

            if (anySucceeded)
            {
                // 已有可用答案：取消余下端点，不再打扰它们。
                // 未被 await 的任务异常必须被观察，否则会成为 unobserved task exception。
                foreach (var task in pending)
                {
                    _ = task.ContinueWith(
                        t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                }
                winnerCts.Cancel();
            }
            else
            {
                // 全部端点都失败：此时所有任务都已结束，熔断计数已在各自任务内上报。
                foreach (var task in tasks)
                {
                    _ = task.Exception;
                }
            }

            if (anySucceeded == false && cancellationToken.IsCancellationRequested == false)
            {
                this.logger.LogDebug($"{host} type={type}：{available.Count} 个 DoH 端点全部失败");
            }

            return merged.ToArray();
        }

        /// <summary>
        /// 竞速模式下的单端点查询：内部自行完成"重试 + 熔断计数上报"，
        /// 失败一律返回空列表而**不抛异常**，这样 <see cref="Task.WhenAny(Task[])"/>
        /// 可以直接收割任意一个完成任务而无需 try/catch。
        /// </summary>
        /// <param name="endpoint">DoH 端点</param>
        /// <param name="dnsParam">base64url 编码的 DNS 报文字节</param>
        /// <param name="raceToken">竞速令牌；被取消表示已有其它端点胜出或整体停机</param>
        /// <returns>该端点解析到的地址；不可用或失败时为空列表</returns>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointRaceAsync(string endpoint, string dnsParam, CancellationToken raceToken)
        {
            try
            {
                var addresses = await this.QueryEndpointWithRetryAsync(endpoint, dnsParam, raceToken);
                return addresses;
            }
            catch (OperationCanceledException)
            {
                // 竞速令牌被取消：说明已有端点胜出，或整体停机。
                // 两种情况都不该算该端点失败，否则一次正常的竞速胜出会把所有端点熔断。
                return Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug($"DoH 端点 {endpoint} 竞速查询异常：{ex.Message}");
                return Array.Empty<IPAddress>();
            }
        }

        /// <summary>
        /// 该端点是否处于熔断冷却期
        /// </summary>
        private bool IsEndpointBlocked(string endpoint)
        {
            if (this.endpointHealth.TryGetValue(endpoint, out var health) == false)
            {
                return false;
            }

            if (health.BlockedUntil > DateTime.UtcNow)
            {
                return true;
            }

            // 冷却已过，给一次重新试探的机会（清零失败计数）
            this.endpointHealth.TryUpdate(endpoint, (Failures: 0, BlockedUntil: DateTime.MinValue), health);
            return false;
        }

        /// <summary>
        /// 记录端点的成功/失败，连续失败达阈值则熔断
        /// </summary>
        private void ReportEndpointResult(string endpoint, bool success)
        {
            if (success)
            {
                if (this.endpointHealth.TryRemove(endpoint, out _))
                {
                    // 该端点曾被判死，现已恢复可用：清掉落盘记录，避免下次启动继续跳过它
                    this.PersistEndpointHealth();
                }
                return;
            }

            var before = this.endpointHealth.TryGetValue(endpoint, out var current)
                ? current
                : (Failures: 0, BlockedUntil: DateTime.MinValue);

            (int Failures, DateTime BlockedUntil) after = before.BlockedUntil > DateTime.UtcNow
                // 已在冷却中，保持原有的剩余时长，不要被本次失败无限延长
                ? before
                : (before.Failures + 1, before.Failures + 1 >= endpointFailureThreshold
                    ? DateTime.UtcNow.Add(endpointCooldown)
                    : DateTime.MinValue);

            if (this.endpointHealth.TryUpdate(endpoint, after, before))
            {
                // 新进入熔断时立即落盘（保证进程被杀/断电也不丢）
                if (before.BlockedUntil <= DateTime.UtcNow && after.BlockedUntil > DateTime.UtcNow)
                {
                    this.PersistEndpointHealth();
                    this.logger.LogWarning($"DoH 端点 {endpoint} 连续失败 {after.Failures} 次，已熔断 {endpointCooldown.TotalMinutes:F0} 分钟");
                }
            }
        }

        /// <summary>
        /// 向单个 DoH 端点发送 wire 格式查询（带 1 次重试），解析响应。
        /// <para>单次 DoH 请求可能因网络瞬时拥塞超时；重试一次即可覆盖大多数瞬时抖动，
        /// 又不至于把整体解析拖得太久（重试预算仍受 httpClient.Timeout 约束）。</para>
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointWithRetryAsync(string endpoint, string dnsParam, CancellationToken cancellationToken)
        {
            const int maxTries = 2;
            for (var attempt = 0; attempt < maxTries; attempt++)
            {
                var result = await QueryEndpointAsync(endpoint, dnsParam, cancellationToken);
                if (result.Count > 0)
                {
                    this.ReportEndpointResult(endpoint, success: true);
                    return result;
                }

                // 注意：被取消（停机）不算端点失败，否则一次正常关闭会把所有端点熔断 10 分钟。
                if (cancellationToken.IsCancellationRequested)
                {
                    return Array.Empty<IPAddress>();
                }

                if (attempt < maxTries - 1)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400d), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // 竞速模式下另一端点已胜出：退避等待被取消是正常路径，不计失败。
                        return Array.Empty<IPAddress>();
                    }
                }
            }

            this.ReportEndpointResult(endpoint, success: false);
            return Array.Empty<IPAddress>();
        }

        /// <summary>
        /// 向单个 DoH 端点发送 wire 格式查询并解析响应
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointAsync(string endpoint, string dnsParam, CancellationToken cancellationToken)
        {
            // 全局闸门：把竞速产生的并发突发压到 DohRequestConcurrency 个。
            // WaitAsync 前先看取消，避免停机时还在排队。
            try
            {
                await this.requestGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<IPAddress>();
            }

            try
            {
                var url = $"{endpoint}?dns={dnsParam}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("accept", "application/dns-message");
                using var response = await this.httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode == false)
                {
                    // 【不要静默吞掉非200】此前一律返回空列表，最终只报"所有端点均不可用"，
                    // 真实原因（400=报文格式非法、404=路径不对、502=被代理拦截）完全不可见。
                    // DoH 曾因 DNS 报文字节序错误长期 400，而日志只显示"端点不可用"，极难定位。
                    this.logger.LogDebug($"DoH 端点 {endpoint} 返回 {(int)response.StatusCode} {response.StatusCode}");
                    return Array.Empty<IPAddress>();
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return ParseResponse(bytes);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug($"DoH 端点 {endpoint} 查询失败：{ex.Message}");
                return Array.Empty<IPAddress>();
            }
            finally
            {
                this.requestGate.Release();
            }
        }

        /// <summary>
        /// 构造一个最简 DNS 查询报文（RD=1，单问题）
        /// <para>
        /// 【关键】所有 16 位字段必须按**大端序**（网络字节序）写入。
        /// 原实现用 <c>BinaryWriter.Write(ushort)</c>，而它写的是**小端序**：
        /// FLAGS 0x0100 被写成 00 00 01 00、QTYPE/QCLASS 被写成 01 00 00 01，
        /// 整个头部字节序错位，DoH 服务端收到格式非法的报文后一律返回 **HTTP 400**。
        /// 表现是"DoH 解析失败：所有端点均不可用"、日志里 0 次成功，
        /// 而实际上网络与端点完全正常（实测阿里 DoH 直连 462ms 正常返回）——
        /// 于此同时明文 DNS 又被 RST，整个解析层两条路同时失效。
        /// </para>
        /// </summary>
        private static byte[] BuildRequest(string host, ushort type)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            // 显式大端写入，避免 BinaryWriter 的小端序
            void WriteUInt16(ushort value)
            {
                writer.Write((byte)(value >> 8));
                writer.Write((byte)(value & 0xFF));
            }

            // Header: ID(2) + Flags(2)=0x0100(RD) + QDCOUNT(2)=1 + ANCOUNT/NSCOUNT/ARCOUNT
            WriteUInt16(0x0000);// ID
            WriteUInt16(0x0100);      // Flags: RD=1（递归查询）
            WriteUInt16(0x0001);      // QDCOUNT=1
            WriteUInt16(0x0000);      // ANCOUNT=0
            WriteUInt16(0x0000);      // NSCOUNT=0
            WriteUInt16(0x0000);      // ARCOUNT=0

            // QNAME: 长度前缀标签序列，以 0 结尾
            foreach (var label in host.Split('.'))
            {
                var labelBytes = Encoding.ASCII.GetBytes(label);
                if (labelBytes.Length == 0 || labelBytes.Length > 63)
                {
                    // 标签长度非法（空标签出现在连续点或首尾点），返回空报文由调用方判失败
                    return Array.Empty<byte>();
                }
                writer.Write((byte)labelBytes.Length);
                writer.Write(labelBytes);
            }
            writer.Write((byte)0x00);

            // QTYPE + QCLASS(IN=1)
            WriteUInt16(type);
            WriteUInt16(0x0001);

            writer.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// 解析 DNS 响应，提取 A(1)/AAAA(28) 记录的 IP
        /// </summary>
        private static IReadOnlyList<IPAddress> ParseResponse(byte[] bytes)
        {
            var addresses = new List<IPAddress>();
            if (bytes == null || bytes.Length < 12)
            {
                return addresses;
            }

            // Header 12 字节，随后跳过 QDCOUNT 个问题段
            var qdCount = (bytes[4] << 8) | bytes[5];
            var anCount = (bytes[6] << 8) | bytes[7];
            var position = 12;
            for (var i = 0; i < qdCount && position < bytes.Length; i++)
            {
                position = SkipName(bytes, position);
                position += 4; // QTYPE(2) + QCLASS(2)
            }

            for (var i = 0; i < anCount && position + 10 <= bytes.Length; i++)
            {
                position = SkipName(bytes, position);
                var type = (ushort)((bytes[position] << 8) | bytes[position + 1]);
                position += 2;
                position += 2; // CLASS
                position += 4; // TTL
                var rdLength = (ushort)((bytes[position] << 8) | bytes[position + 1]);
                position += 2;

                if ((type == 1 || type == 28) && position + rdLength <= bytes.Length)
                {
                    try
                    {
                        var ip = new IPAddress(bytes.Skip(position).Take(rdLength).ToArray());
                        if (IpAddressFilter.IsInvalid(ip) == false)
                        {
                            addresses.Add(ip);
                        }
                    }
                    catch (Exception)
                    {
                        // 长度或格式异常，忽略该记录
                    }
                }
                position += rdLength;
            }

            return addresses;
        }

        /// <summary>
        /// 跳过 DNS 报文中的 NAME 字段（兼容 0xC0 压缩指针）
        /// </summary>
        private static int SkipName(byte[] bytes, int position)
        {
            while (position < bytes.Length)
            {
                var length = bytes[position];
                if (length == 0)
                {
                    return position + 1;
                }

                // 压缩指针：高两位为 11，指向其它位置，NAME 仅占 2 字节
                if ((length & 0xC0) == 0xC0)
                {
                    return position + 2;
                }

                position += length + 1;
            }
            return position;
        }

        /// <summary>
        /// 转为无填充的 base64url
        /// </summary>
        private static string ToBase64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
