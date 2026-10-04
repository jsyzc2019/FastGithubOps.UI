using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed class DohResolver
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
        /// 单次 DoH 请求的**上界**预算。
        /// <para>
        /// 本机网络到 DoH 端点的 RTT 可能达数秒（与到 GitHub 的 RTT 同源），8s 曾在拥塞时
        /// 把"慢但可用"的国内端点判死，导致所有端点均不可用。
        /// <para>
        /// 【v2.6.6 P1-1】这是**上界**而非固定值：实际预算由
        /// <see cref="GetTimeoutFor"/> 按该端点实测 RTT 在
        /// [<see cref="minAdaptiveTimeout"/>, 本值] 区间内动态给出。
        /// 快端点只会多等几秒，而慢端点不会因为被压到秒级预算而误判为死端点。
        /// </para>
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
        /// 【v2.6.6 P0-1】解析失败后的退避基数（首次失败后等这么久才允许再试）。
        /// <para>
        /// 【为什么必须有】原实现只有 10 分钟的**正**缓存，失败路径**完全没有节流**：
        /// <c>DomainResolveHostedService</c> 每秒跑一轮全量测速，18 个域名各自发一次
        /// A+AAAA 竞速（5 端点 = 10 个请求），网络一坏就变成
        /// "18 域名 × 每 2 秒 × 10 请求" 的无限重打。
        /// v2.6.5 长时日志实测：该窗口持续 **98 分钟**，产生 **11,834 条**
        /// "DoH 解析失败：所有端点均不可用"，而同期仍有 709 次解析成功 ——
        /// 说明网络只是**部分**可达（部分端点/部分记录类型在闪断），
        /// 这种"半通不通"状态恰好是最坏情况：不彻底失败触发任何保护，
        /// 又让重试以最快频率持续消耗。
        /// </para>
        /// </summary>
        private static readonly TimeSpan negativeBackoffBase = TimeSpan.FromSeconds(2d);

        /// <summary>
        /// 退避上限。取 60 秒：与正缓存 10 分钟、端点熔断 10 分钟同量级但更短，
        /// 保证网络恢复后**最迟 1 分钟**就能重新拿到新 IP，不会因退避而"修完就废"。
        /// </summary>
        private static readonly TimeSpan negativeBackoffMax = TimeSpan.FromSeconds(60d);

        /// <summary>
        /// 使用 IP 字面量（而非域名）作为 DoH 端点，使客户端无需先解析域名，
        /// 彻底绕开「明文 DNS 被 RST」这一原始故障点。
        /// 证书校验在此处放宽（直连 IP 时 SNI 为 IP，无法匹配服务商证书域名）；
        /// 取回的 IP 会在 IPAddressService 中再做 TCP+TLS 实测，真正连 GitHub 时的
        /// 证书链校验仍由代理层执行，放宽 DoH 一侧不会带来实际安全风险。
        /// </summary>
        private static readonly string[] Endpoints = new[]
        {
            // 【v2.6.4 端点列表重建】按 2026-10-03 本机直连实测（每端点 3 次，UseProxy=false 等价）
            // 重新排序并补全。旧列表只有 4 个端点，其中 **3 个在本机稳定 12 秒超时**，
            // 唯一可达的 223.5.5.5 又独自承担全部解析；一旦它抖动，
            // 单域名首查就会白等慢端点的超时预算，直接顶穿交互域名的 25s 预算。
            //
            // 实测结果（github.com A 记录，wire 格式）：
            //   OK   阿里 223.5.5.5      123~173ms  avg=148ms
            //   OK   百度 doh.pub        117~268ms  avg=172ms
            //   OK   360  doh.360.cn     163~180ms  avg=170ms
            //   FAIL 腾讯 119.29.29.29   Timeout（12s）
            //   FAIL Cloudflare 1.1.1.1  Timeout（12s）
            //   FAIL Google 8.8.8.8      Timeout（12s）
            //   FAIL Quad9 9.9.9.9       Timeout（12s）
            //
            // 【为什么不保留腾讯 119.29.29.29】旧列表里有它，但实测直连 12 秒稳定超时，
            // 且**换成域名形式也未必可达**（doh.pub 属百度、不是腾讯；腾讯的 DoH 域名在
            // 本网络同样不可达）。保留一个已确认失效的端点只会让每轮竞速多等 12 秒，
            // 因此直接移除。若换网络后腾讯恢复，需要时再加回即可。
            //
            // 【为什么百度/360 用域名而非 IP】这两家未公开可直接访问的 IP 字面量端点，
            // 且其证书只对域名签发。走域名意味着要先解析一次 —— 但这正是 DoH 客户端
            // 的标准做法，且 System.Net 的 DNS 走的是本机解析链路；
            // 万一本机明文 DNS 被 RST（已知问题），DoH 端点域名会解析失败，
            // 此时该端点自动失败并被熔断，**阿里 223.5.5.5（IP 字面量）仍是保底**。
            "https://223.5.5.5/dns-query",     // 阿里 DNS，IP 直连，绕开本机 DNS
            "https://doh.pub/dns-query",       // 百度 DoH
            "https://doh.360.cn/dns-query",    // 360 DoH
            "https://1.1.1.1/dns-query",       // Cloudflare（多数网络下被阻断，靠熔断剔除）
            "https://8.8.8.8/dns-query",       // Google（同上）
        };

        /// <summary>
        /// 全局 DoH 在途请求上限。
        /// <para>
        /// 【闸门语义（v2.6.4 修正）】只约束"**同时握有连接的请求数**"，不排队等待。
        /// 原实现把闸门覆盖到整个网络等待期，导致慢端点会占住槽位长达 12 秒，
        /// 把 148ms 就能返回的快端点挤在门外干等 —— 这才是首查 32 秒的真凶。
        /// 现在的做法：<see cref="QueryEndpointAsync"/> 在**发起瞬间**占闸门、
        /// 发起后立刻释放，因此
        ///   1) 在途连接数始终 ≤ <see cref="DohRequestConcurrency"/>，跨境端点全挂时
        ///      不会堆积 5 端点 × 2 记录类型 × 15 域名 = 150 个连接；
        ///   2) 快端点永远不必排在慢端点后面等槽位。
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim requestGate = new(DohRequestConcurrency, DohRequestConcurrency);

        /// <summary>
        /// DoH 在途请求并发上限，取 12。
        /// <para>
        /// 5 个端点 × 2 种记录类型 = 单域名最多 10 个在途，12 刚好容得下"一次完整竞速"，
        /// 不至于让 A 与 AAAA 两轮串行。实测可达端点 RTT 仅 117~268ms，
        /// 12 路远高于端点 RTT，不构成瓶颈。
        /// </para>
        /// </summary>
        private const int DohRequestConcurrency = 12;

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
        /// 各 DoH 端点的熔断状态。
        /// <para>
        /// 【v2.6.6 P0-2 关键修复】从 <c>(int, DateTime)</c> 值类型元组改为**引用类型 + lock**。
        /// 原实现用 <c>ConcurrentDictionary.TryUpdate</c> 做"读-改-写"：
        /// <code>before = 读; after = before+1; TryUpdate(k, after, before)</code>
        /// 这在**并发**下必然丢计数 —— TryUpdate 只在值恰好等于 before 时才写入，
        /// 18 个域名 × A/AAAA 并发时几十个任务同时读同一个 Failures=0，
        /// 只有 1 个 CAS 成功，其余全部静默失败，计数永远停在 1。
        /// 现象是：日志里 11,834 次"所有端点均不可用"，但"已熔断"日志 **0 条**、
        /// <c>doh_blocked_endpoints.json</c> 从未生成 —— 熔断机制形同虚设。
        /// 改为 lock 保护的可变对象后，累加是原子的，不再丢计数。
        /// </para>
        /// </summary>
        private readonly ConcurrentDictionary<string, EndpointState> endpointHealth = new();

        /// <summary>
        /// 【v2.6.6 P0-1】解析失败后的负缓存（退避）：值为 (连续失败次数, 允许重试的时刻)。
        /// <para>
        /// 语义与<a cref="positiveCache"/>相反：正缓存是"成功了就复用"，
        /// 负缓存是"失败了先别打了"。缺了它，失败路径就是无节流的无限重试。
        /// </para>
        /// </summary>
        private readonly ConcurrentDictionary<string, (int Failures, DateTime RetryAfter)> negativeCache = new();

        private sealed class EndpointState
        {
            /// <summary>连续失败次数</summary>
            public int Failures;

            /// <summary>熔断截止时刻，未熔断时为 <see cref="DateTime.MinValue"/></summary>
            public DateTime BlockedUntil = DateTime.MinValue;

            /// <summary>
            /// 【v2.6.6 P1-1】该端点的 RTT 指数滑动平均（毫秒），0 表示尚无成功样本。
            /// <para>用于按端点实际快慢动态计算超时，而非全局固定值。</para>
            /// </summary>
            public double RttEma;

            /// <summary>保护上述字段的读-改-写</summary>
            public readonly object SyncRoot = new object();
        }

        /// <summary>
        /// 【v2.6.6 P1-1】自适应超时的下界：3 秒。
        /// <para>实测可用端点 RTT 仅 121~443ms，3 秒仍有 7~25 倍余量，
        /// 足以吸收网络抖动，又远小于交互域名的 25s 整体预算。</para>
        /// </summary>
        private static readonly TimeSpan minAdaptiveTimeout = TimeSpan.FromSeconds(3d);

        /// <summary>
        /// 【v2.6.6 P1-1】自适应超时的上界，与 <see cref="perRequestTimeout"/> 同值，
        /// 避免出现第二个"最大超时"定义而两者失配。
        /// <para>不再放宽：交互域名整体预算 25s、usercontent 120s，
        /// 单端点再往上加只会挤占重试机会而不会提高成功率。</para>
        /// </summary>
        private static readonly TimeSpan maxAdaptiveTimeout = perRequestTimeout;

        /// <summary>
        /// 超时 = 该端点实测 RTT 的多少倍。
        /// <para>取 3 倍：DoH 响应体只有几十字节，正常情况下 RTT 就是全部耗时，
        /// 3 倍余量足以覆盖 TCP 重传一次（~1 个 RTT）与调度抖动。
        /// 倍数过小会在抖动时把好端点误判为死（进而触发熔断），
        /// 倍数过大则失去"尽早发现死端点"的意义。</para>
        /// </summary>
        private const double adaptiveTimeoutFactor = 3d;

        /// <summary>
        /// RTT 指数滑动平均中"新样本"的权重。
        /// <para>取 0.3 而非 0.5：网络劣化是**渐变**过程（v2.6.5 长时日志实测 6 小时内
        /// RTT 变化一个数量级），权重过高会让单次抖动把均值带偏并立刻反映到超时上；
        /// 0.3 既能在约 5 次样本内跟上劣化，又不会被一次偶发慢包放大。</para>
        /// </summary>
        private const double rtaEmaWeight = 0.3d;

        /// <summary>
        /// 按端点实测 RTT 计算该端点的超时预算。
        /// <para>
        /// 【为什么必须自适应，而不是调大固定值】v2.6.5 长时日志里同一批 DoH 端点的
        /// RTT 在 6 小时内变化了一个数量级 —— 任何固定常数在网络环境变化后都会失配：
        /// 定得小则把慢端点判死并熔断，定得大则让真死端点拖满预算。
        /// 自适应让"多等一会儿"的代价只落在**本身较慢**的端点上，
        /// 而快端点仍以百毫秒级返回，两者互不影响。
        /// </para>
        /// </summary>
        /// <param name="endpoint">端点</param>
        /// <returns>该端点本次请求的超时预算（已夹在 <see cref="minAdaptiveTimeout"/> 与 <see cref="maxAdaptiveTimeout"/> 之间）</returns>
        private TimeSpan GetTimeoutFor(string endpoint)
        {
            var state = this.endpointHealth.GetOrAdd(endpoint, _ => new EndpointState());
            lock (state.SyncRoot)
            {
                if (state.RttEma <= 0d)
                {
                    // 尚无成功样本（首次访问或一直失败）：退到上界，
                    // 不因为"还不知道有多快"而先斩断它的机会。
                    return maxAdaptiveTimeout;
                }

                var scaled = TimeSpan.FromMilliseconds(state.RttEma * adaptiveTimeoutFactor);
                if (scaled < minAdaptiveTimeout)
                {
                    return minAdaptiveTimeout;
                }

                return scaled > maxAdaptiveTimeout ? maxAdaptiveTimeout : scaled;
            }
        }

        /// <summary>
        /// 用本次成功响应的耗时更新端点的 RTT 滑动平均。
        /// </summary>
        private void ReportEndpointRtt(string endpoint, double elapsedMilliseconds)
        {
            var state = this.endpointHealth.GetOrAdd(endpoint, _ => new EndpointState());
            lock (state.SyncRoot)
            {
                state.RttEma = state.RttEma <= 0d
                    ? elapsedMilliseconds
                    : state.RttEma * (1d - rtaEmaWeight) + elapsedMilliseconds * rtaEmaWeight;
            }
        }

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
                // 【v2.6.6 P0-2】Timeout 设为 Infinite：超时**只**由每端点的 endpointCts 管控。
                // 原先 HttpClient.Timeout 与 endpointCts 是两套并行的超时，
                // HttpClient 的那个会先触发并抛 TaskCanceledException，
                // 而此时 endpointCts 尚未取消 —— 上层据此判定"不是端点超时、不计失败"，
                // 真实失败信号又一次丢失。统一到单一令牌后，超时只有一个来源、一种含义。
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
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
                            this.endpointHealth[endpoint] = new EndpointState
                            {
                                Failures = endpointFailureThreshold,
                                BlockedUntil = until
                            };
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
            // 说明：BlockedUntil 的读取不在 lock 内。此处刻意保持无锁 ——
            // 落盘是尽力而为的旁路（即便读到撕裂的旧值，下次启动也只是多/少跳过一个端点），
            // 权威状态始终在内存的 endpointHealth 里，锁只用于保证计数不丢。
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
        /// <para>
        /// 【v2.6.6 P0-1】**同时清除失败退避**。这是刻意的例外：
        /// 退避是为了掐断"后台每秒机械重试"，而本方法恰恰是用户/健康度触发的
        /// **主动恢复**尝试 —— 网络可能刚刚恢复，若此时还因退避返回空，
        //"恢复"就变成空转，退避反而成了阻碍恢复的元凶。
        /// 调用方（<c>DomainResolver.RefreshAsync</c>）自身已有 10 秒冷却与 IP 拉黑门槛，
        /// 不会退化成无限重试，因此这里清空是安全的。
        /// </para>
        /// </summary>
        public void InvalidateCache()
        {
            this.positiveCache.Clear();
            this.inflight.Clear();

            // 主动恢复路径不受失败退避约束（理由见方法注释）
            this.negativeCache.Clear();
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

            // 【v2.6.6 P0-1】失败退避：处于退避期内直接返回空，不发起任何 DoH 请求。
            // 这才是真正掐断重解析风暴的那一刀 —— 上层（后台每秒测速）照常轮询，
            // 但网络坏时不会再有流量打向 DoH 端点。
            if (this.negativeCache.TryGetValue(host, out var negative) && negative.RetryAfter > DateTime.UtcNow)
            {
                return Array.Empty<IPAddress>();
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
            if (list.Length > 0)
            {
                this.positiveCache[host] = (DateTime.UtcNow.Add(positiveCacheTtl), list);

                // 成功即清零退避，网络恢复后立刻回到"每次都查"的正常节奏
                this.ClearNegativeCache(host);
                this.logger.LogInformation($"DoH 解析 {host} 成功，得 {list.Length} 个候选IP");
            }
            else
            {
                // 【v2.6.6 P0-1】失败进入指数退避，并按"首次/重复"分级日志。
                // 原实现每次都打 LogWarning，98 分钟故障窗口刷出 11,834 条同文案警告，
                // 真正需要关注的首条失败与端点级原因（返回码/异常）反而被噪声淹没。
                // 现在：每段退避序列的首次失败仍告警（Warning），重复的降为 Debug。
                this.ReportNegative(host, out var backoff, out var isFirstFailure);
                if (isFirstFailure)
                {
                    this.logger.LogWarning(
                        $"DoH 解析 {host} 失败：所有端点均不可用，{backoff.TotalSeconds:F0} 秒后重试（连续失败将指数退避，上限 60 秒）");
                }
                else
                {
                    this.logger.LogDebug(
                        $"DoH 解析 {host} 再次失败，退避至 {DateTime.UtcNow.Add(backoff):HH:mm:ss} 后重试");
                }
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
                // 【v2.6.6 P0-1】降为 Debug：全熔断是熔断生效后的**正常稳态**，
                // 每秒 18 个域名各打一条 Warning 只会刷屏，真正的告警已在熔断那一刻发过。
                this.logger.LogDebug("所有 DoH 端点均处于熔断冷却期，本轮不发起查询");
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
            if (this.endpointHealth.TryGetValue(endpoint, out var state) == false)
            {
                return false;
            }

            lock (state.SyncRoot)
            {
                if (state.BlockedUntil > DateTime.UtcNow)
                {
                    return true;
                }

                // 冷却已过，给一次重新试探的机会（清零失败计数）
                if (state.Failures > 0 || state.BlockedUntil != DateTime.MinValue)
                {
                    state.Failures = 0;
                    state.BlockedUntil = DateTime.MinValue;
                }

                return false;
            }
        }

        /// <summary>
        /// 记录端点的成功/失败，连续失败达阈值则熔断。
        /// <para>
        /// 【v2.6.6 P0-2】读-改-写全程在 <c>state.SyncRoot</c> 内完成。
        /// 原实现用 ConcurrentDictionary.TryUpdate 做 CAS，并发下会静默丢计数
        /// （几十个域名同时失败，只有一个能写入），导致熔断永不触发。
        /// 现在用 lock，累加保证不丢；成功/失败的相互覆盖也按时间顺序正确处理。
        /// </para>
        /// <para>
        /// 【v2.6.6 P1-1】成功时**只清熔断状态、保留 RTT 统计**。
        /// 原实现用 <c>TryRemove</c> 整条删除，端点状态同时承担熔断与 RTT 两个职责，
        /// 于是每次成功都会把 RTT 历史一起抹掉 → 下次 GetTimeoutFor 又退回
        /// "无样本" 的 12s 上界，**自适应完全失效**（恰好在端点最健康、最该快的时候失效）。
        /// </para>
        /// </summary>
        private void ReportEndpointResult(string endpoint, bool success)
        {
            var state = this.endpointHealth.GetOrAdd(endpoint, _ => new EndpointState());
            if (success)
            {
                var shouldPersist = false;
                lock (state.SyncRoot)
                {
                    // 只清熔断相关字段，RttEma 保留 —— 它反映的是这个端点的客观快慢，
                    // 与"这次是否成功"无关，不该被一次成功抹掉。
                    shouldPersist = state.BlockedUntil > DateTime.UtcNow || state.Failures > 0;
                    state.Failures = 0;
                    state.BlockedUntil = DateTime.MinValue;
                }

                if (shouldPersist)
                {
                    // 该端点曾被判死，现已恢复可用：清掉落盘记录，避免下次启动继续跳过它
                    this.PersistEndpointHealth();
                }
                return;
            }

            var newlyBlocked = false;
            lock (state.SyncRoot)
            {
                if (state.BlockedUntil > DateTime.UtcNow)
                {
                    // 已在冷却中，保持原有的剩余时长，不要被本次失败无限延长
                    return;
                }

                state.Failures++;
                if (state.Failures >= endpointFailureThreshold)
                {
                    state.BlockedUntil = DateTime.UtcNow.Add(endpointCooldown);
                    newlyBlocked = true;
                }
            }

            // 新进入熔断时立即落盘（保证进程被杀/断电也不丢）
            if (newlyBlocked)
            {
                this.PersistEndpointHealth();
                this.logger.LogWarning($"DoH 端点 {endpoint} 连续失败 {endpointFailureThreshold} 次，已熔断 {endpointCooldown.TotalMinutes:F0} 分钟");
            }
        }

        /// <summary>
        /// 记录一次解析失败并返回退避时长与是否为首次失败。
        /// <para>
        /// 【v2.6.6 P0-1】指数退避：2s → 4s → 8s → 16s → 32s → 60s（封顶）。
        /// 成功一次即清零（见 <see cref="ClearNegativeCache"/>）。
        /// </para>
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="backoff">下次允许重试前的静默时长</param>
        /// <param name="isFirstFailure">本次调用是否为该退避序列的**首次**失败（用于日志分级）</param>
        private void ReportNegative(string host, out TimeSpan backoff, out bool isFirstFailure)
        {
            // 先判断是否已处于退避期：这才是"重复失败"的准确判据。
            // 【为什么不能由计数反推】首次失败会把 Failures 置为 1，而退避期内的重复失败
            // 既不叠加计数（仍为 1）也不延长退避 —— 于是"计数 <= 1"在首次与重复两种
            // 情形下**同样成立**，用它判首次会让退避期内每次失败都升级为 Warning，
            // 降噪完全失效（这正是自测首次运行抓出的问题）。
            var existed = this.negativeCache.TryGetValue(host, out var previous);
            var inBackoffWindow = existed && previous.RetryAfter > DateTime.UtcNow;

            var updated = this.negativeCache.AddOrUpdate(
                host,
                // 首次失败：退避一个基数时长
                _ => (Failures: 1, RetryAfter: DateTime.UtcNow.Add(negativeBackoffBase)),
                (_, current) => inBackoffWindow
                    // 退避期内的重复失败不叠加计数，也不延长退避
                    ? current
                    : (Failures: current.Failures + 1, RetryAfter: DateTime.UtcNow.Add(ComputeBackoff(current.Failures + 1))));

            // 首次 = 该域名**从未失败过**。退避过期的再次失败属于"又一次失败"，
            // 同样降为 Debug —— 若把它也算首次，同一个老域名在故障期内会反复告警，
            // 而 11,834 条噪声正是这样累积出来的。只有"新域名首次失败"才值得打扰用户。
            isFirstFailure = existed == false;

            var remaining = updated.RetryAfter - DateTime.UtcNow;
            backoff = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>
        /// 计算第 n 次连续失败后的退避时长（2s 起，指数增长，60s 封顶）
        /// </summary>
        private static TimeSpan ComputeBackoff(int failures)
        {
            // failures=1 → 2s, 2 → 4s, 3 → 8s ... 用位移避免 Math.Pow 的浮点误差
            var shift = Math.Min(failures - 1, 20);
            var seconds = negativeBackoffBase.TotalSeconds * (1 << shift);
            return seconds >= negativeBackoffMax.TotalSeconds
                ? negativeBackoffMax
                : TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// 清除某 host 的失败退避（解析成功后调用）
        /// </summary>
        private void ClearNegativeCache(string host)
        {
            this.negativeCache.TryRemove(host, out _);
        }

        /// <summary>
        /// 向单个 DoH 端点发送 wire 格式查询（带 1 次重试），解析响应。
        /// <para>单次 DoH 请求可能因网络瞬时拥塞超时；重试一次即可覆盖大多数瞬时抖动，
        /// 又不至于把整体解析拖得太久（重试预算受 <c>perRequestTimeout</c> 约束 ——
        /// 【v2.6.6】该预算由本方法的 endpointCts 独立持有，不再依赖 httpClient.Timeout）。</para>
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointWithRetryAsync(string endpoint, string dnsParam, CancellationToken raceToken)
        {
            // 【v2.6.6 P0-2 关键修复】把"端点自身失败"与"竞速被取消"彻底解耦。
            //
            // 【原实现的 bug】用同一个 cancellationToken（= winnerCts.Token）同时表达两件事：
            //   1) 这个端点自己超时/报错（真实失败，必须计入熔断计数）
            //   2) 别的端点已经赢了，本端点被无谓取消（与端点健康度无关，绝不能计入）
            // 竞速是常态：国内端点 148ms 就返回，而跨境端点要等满 12s 超时。
            // 一旦有端点胜出，winnerCts.Cancel() 会把还在等超时的端点全部取消，
            // 而 `if (cancellationToken.IsCancellationRequested) return;` 恰好命中 2)，
            // 于是**跨境死端点的失败信号被系统性丢弃** —— 熔断永远攒不够次数。
            // 叠加 CAS 丢计数，熔断 98 分钟内一次都没触发过（v2.6.5 日志实测）。
            //
            // 【现在的做法】把"端点超时"与"竞速取消"拆成两个独立的取消源：
            //   - endpointCts：只负责 perRequestTimeout，端点自己的超时，与竞速无关；
            //   - raceToken：竞速令牌，只用于"别人赢了就别耗着了"。
            // 只有 endpointCts 超时（真实失败）才上报熔断；
            // raceToken 取消（他人胜出）**不产生任何信号** —— 既不记失败（避免误熔慢端点），
            // 也不记成功（否则会把跨境死端点的失败计数清零，熔断永远攒不够次数）。
            // 【v2.6.6 P1-1】超时预算按端点实测 RTT 动态计算，而不是全局固定 12s。
            // v2.6.5 长时日志显示同一批端点的 RTT 在 6 小时内变化了一个数量级，
            // 固定常数必然在网络环境变化后失配：定小则误判慢端点为死并熔断，
            // 定大则让真死端点拖满预算。此处取"该端点当前应有"的预算，
            // 快端点仍以百毫秒级返回，只有本身较慢的端点才会多等。
            //
            // 【每次尝试单独取预算】重试的意义就是"再给一次机会"，
            // 若两次尝试共享一个 CancellationTokenSource，第二次启动时剩余预算可能已所剩无几，
            // 等于没有重试。
            const int maxTries = 2;
            for (var attempt = 0; attempt < maxTries; attempt++)
            {
                var timeout = GetTimeoutFor(endpoint);
                using var endpointCts = new CancellationTokenSource(timeout);
                var startedAt = Stopwatch.GetTimestamp();

                var result = await QueryEndpointAsync(endpoint, dnsParam, endpointCts.Token, raceToken, timeout);
                if (result.Count > 0)
                {
                    // 只有拿到答案的那次请求才计入 RTT 统计：
                    // 超时请求的耗时恒等于超时预算，把它算进去会让 RTT 单调爬升到上界。
                    this.ReportEndpointRtt(endpoint, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                    this.ReportEndpointResult(endpoint, success: true);
                    return result;
                }

                // 端点自身超时 = 真实失败，重试一次再判定
                if (attempt < maxTries - 1 && endpointCts.IsCancellationRequested == false && raceToken.IsCancellationRequested == false)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400d), raceToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // 退避等待期间竞速已结束：这是"别人赢了"，本端点并未暴露任何失败证据。
                        // 【重要】这里**既不记成功也不记失败**：
                        //   记成功 → 慢端点的历史失败被清零，跨境死端点永远攒不够次数而永不熔断；
                        //   记失败 → 一次正常竞速胜出就把所有慢端点误判为坏端点。
                        // 正确处置是"无信号"，让端点健康度只被真实的请求结果驱动。
                        return Array.Empty<IPAddress>();
                    }

                    continue;
                }

                break;
            }

            // raceToken 已取消 = 两种情形之一：
            //   a) 别的端点已胜出 → 本端点没有暴露任何失败证据，不计失败（否则跨境慢端点会被系统性误熔）
            //   b) 整体停机 → 同样不计失败（否则一次正常关闭会把所有端点熔断 10 分钟）
            // 两种情形的处置一致，因此无需区分。
            if (raceToken.IsCancellationRequested)
            {
                return Array.Empty<IPAddress>();
            }

            // 走到这里说明：端点自身超时（endpointCts 触发）或全部尝试均返回空，
            // 且竞速未结束 —— 这是端点的真实失败，计入熔断计数。
            this.ReportEndpointResult(endpoint, success: false);
            return Array.Empty<IPAddress>();
        }

        /// <summary>
        /// 向单个 DoH 端点发送 wire 格式查询并解析响应
        /// <para>
        /// 【v2.6.6 P0-2】<paramref name="endpointToken"/> 与 <paramref name="raceToken"/> 分离：
        /// 前者只承载本端点的超时（真实失败的唯一来源），后者只承载"他人已胜出/整体停机"。
        /// 二者任一触发都返回空列表，但**含义完全不同**，由调用方据此决定是否计入熔断。
        /// </para>
        /// </summary>
        /// <param name="endpointToken">本端点超时令牌（真实失败的唯一来源）</param>
        /// <param name="raceToken">竞速令牌（他人胜出 / 整体停机）</param>
        /// <param name="timeout">本次请求实际生效的超时预算，仅用于日志展示</param>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointAsync(
            string endpoint,
            string dnsParam,
            CancellationToken endpointToken,
            CancellationToken raceToken,
            TimeSpan timeout)
        {
            // 【v2.6.4 关键修正】闸门只保护"发起请求"这个瞬时动作，**不覆盖整个等待过程**。
            //
            // 【为什么必须这样】原实现是"先占闸门 → 发请求 → 等满 perRequestTimeout → 释放"。
            // 但闸门是**全局共享**的：慢端点一旦进入，就会占住 1 个槽位长达 12 秒
            // （本机实测腾讯/Cloudflare/Google/Quad9 全部 12 秒超时）。
            // 冷启动时 15 个域名 × 5 个端点共 75 个请求抢 8 个闸门槽位，
            // 排在后面的**包括仅 148ms 就能返回的阿里端点**也得干等前面慢端点超时释放 ——
            // 这正是 v2.6.4 实测中 github.com 首查仍耗 32 秒、顶穿 25s 预算的直接原因。
            // 闸门本来的目的是"压住并发突发"，而并发突发恰恰发生在**发起**的那一刻，
            // 不是在等待期间 —— 因此只需在发起时短暂持锁。
            var url = $"{endpoint}?dns={dnsParam}";
            HttpRequestMessage request;
            try
            {
                // 闸门只等"竞速取消"（有人赢了就不必发起），不因端点超时而放弃排队 ——
                // 否则一个慢端点正占着闸门时，后来者会被误判。
                await this.requestGate.WaitAsync(raceToken);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<IPAddress>();
            }

            try
            {
                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("accept", "application/dns-message");
            }
            finally
            {
                // 发起即释放：后续的网络等待不再占用闸门槽位。
                this.requestGate.Release();
            }

            // 【v2.6.6 P0-2】链接两个令牌：
            //   - raceToken 取消 → 他人胜出/停机，HttpClient 抛 TaskCanceledException，
            //     此时 endpointCts **未** 触发，调用方据此判定"不计失败"；
            //   - endpointCts 触发 → 本端点真的慢/死了，计入熔断。
            // 靠"两个令牌谁先触发"来区分，比原来靠单一令牌判断准确得多。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(endpointToken, raceToken);
            try
            {
                using (request)
                {
                    using var response = await this.httpClient.SendAsync(request, linkedCts.Token);
                    if (response.IsSuccessStatusCode == false)
                    {
                        // 【不要静默吞掉非200】此前一律返回空列表，最终只报"所有端点均不可用"，
                        // 真实原因（400=报文格式非法、404=路径不对、502=被代理拦截）完全不可见。
                        // DoH 曾因 DNS 报文字节序错误长期 400，而日志只显示"端点不可用"，极难定位。
                        this.logger.LogDebug($"DoH 端点 {endpoint} 返回 {(int)response.StatusCode} {response.StatusCode}");
                        return Array.Empty<IPAddress>();
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
                    return ParseResponse(bytes);
                }
            }
            catch (OperationCanceledException)
            {
                // 端点自身超时（真实失败）必须显式记一条日志：
                // 这是判断"哪个端点坏了"的唯一依据，此前被静默吞掉，
                // 导致 98 分钟故障窗口里一条端点级线索都没有。
                if (endpointToken.IsCancellationRequested && raceToken.IsCancellationRequested == false)
                {
                    this.logger.LogDebug($"DoH 端点 {endpoint} 超时（本次预算 {timeout.TotalSeconds:F1}s）");
                }

                return Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                this.logger.LogDebug($"DoH 端点 {endpoint} 查询失败：{ex.Message}");
                return Array.Empty<IPAddress>();
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
