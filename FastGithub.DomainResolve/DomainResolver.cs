using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        private const int MAX_IP_COUNT = 3;

        /// <summary>
        /// 单个域名因IP被拉黑而触发重选的最小间隔
        /// <para>
        /// 取 10 秒（原 30 秒）：本值与「拉黑判定 + 拉黑时长」串联构成恢复时延。
        /// 用户实测反馈被阻断后恢复偏慢，而 DoH 正缓存已有 10 分钟，
        /// 短时间内重复重解析并不会拿到新 IP，只会空转并推迟真正的重选。
        /// 10 秒足以给「坏 IP 让位 + 新 IP 顶上」留出空间，又不会把恢复拖到分钟级。
        /// </para>
        /// </summary>
        private static readonly TimeSpan REFRESH_COOLDOWN = TimeSpan.FromSeconds(10d);

        private readonly ConcurrentDictionary<string, DateTime> refreshCooldown = new();
        private readonly DnsClient dnsClient;
        private readonly PersistenceService persistence;
        private readonly IPAddressService addressService;
        private readonly HostsService hostsService;
        private readonly IpHealthTracker healthTracker;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddress[]> dnsEndPointAddress = new();

        /// <summary>
        /// 解析测速的并发上限
        /// <para>
        /// 取 8：既能把"串行排队"从分钟级压到秒级（实测 9 分钟 -> 数秒），
        /// 又不会像无节制并发那样在启动瞬间对几十个域名同时建连而更像攻击流量。
        /// 每个域名内部的候选IP仍由 IPAddressService 自行串行探测，此处只限"域名间"并发。
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim speedSemaphore = new(8, 8);

        /// <summary>
        /// 域名解析器
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="persistence"></param>
        /// <param name="addressService"></param>
        /// <param name="hostsService"></param>
        /// <param name="healthTracker"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            DnsClient dnsClient,
            PersistenceService persistence,
            IPAddressService addressService,
            HostsService hostsService,
            IpHealthTracker healthTracker,
            ILogger<DomainResolver> logger)
        {
            this.dnsClient = dnsClient;
            this.persistence = persistence;
            this.addressService = addressService;
            this.hostsService = hostsService;
            this.healthTracker = healthTracker;
            this.logger = logger;

            foreach (var endPoint in persistence.ReadDnsEndPoints())
            {
                this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>());
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) && addresses.Length > 0)
            {
                foreach (var address in addresses)
                {
                    yield return address;
                }
            }
            else
            {
                if (this.dnsEndPointAddress.TryAdd(endPoint, Array.Empty<IPAddress>()))
                {
                    await this.persistence.WriteDnsEndPointsAsync(this.dnsEndPointAddress.Keys, cancellationToken);
                }

                // 【关键修复】按需解析的结果必须回写缓存。
                // 原实现只把地址 yield 给调用方就丢弃了，导致两个连带问题：
                //   1) 同一域名的每个请求都要重走一遍完整解析链
                //      （实测 v2.6.2 日志中 api.github.com 在 60 秒内被 DoH 解析 15 次）；
                //   2) 更糟的是 DnsClient 内部按 (dns, 域名) 加了互斥信号量，
                //      后台 TestSpeed 正在解析该域名时，真实请求只能排队等它做完，
                //      25s 请求预算被后台测速白白吃掉 -> 504。
                // 回写后，首个成功请求即让该域名对全进程生效，
                // 后台测速退化为"排序优化"，不再与真实请求争抢。
                var resolved = new List<IPAddress>();
                await foreach (var address in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    resolved.Add(address);
                    yield return address;
                }

                if (resolved.Count > 0)
                {
                    this.dnsEndPointAddress[endPoint] = resolved.ToArray();
                }
            }
        }

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task TestSpeedAsync(CancellationToken cancellationToken = default)
        {
            return this.TestSpeedAsync(hostsOnly: false, cancellationToken);
        }

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="hostsOnly">是否仅使用在线hosts源提供的IP</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task TestSpeedAsync(bool hostsOnly, CancellationToken cancellationToken)
        {
            // 【关键修复】原实现是串行 await 循环，而排序键是 Value.Length（当前IP数）。
            // 启动时几乎所有域名的持久化IP都是空数组，全部并列 Length=0，
            // github.com 往往排到队尾；而每个域名的解析要串行走
            // hosts源 -> DoH(多端点, 每个 perRequestTimeout 12s) -> 明文DNS(逐个, 每个 4s)，
            // 单域名最坏可耗 30s+。于是 github.com 的首次解析被推迟到启动后 9 分钟才完成，
            // 这期间它没有任何候选IP，所有请求直接耗尽 Timeout 预算 -> 504。
            // 实测：19:24 启动 -> 19:24~19:33 共 58 条 504 -> 19:33:43 首次解析成功 -> 504 归零。
            //
            // 改为受控并发（默认 8 路）。要点：
            //   1) 仍然优先处理"当前无IP"的域名，保证它们最先拿到候选（保留原排序意图）；
            //   2) 用 SemaphoreSlim 限流，避免一次性对所有域名并发建连被判定为攻击流量
            //      （这正是 RefreshAsync 注释里反复强调要防的探测风暴）；
            //   3) 单个域名失败只记录并继续，不能拖垮整轮 —— 原串行版本一个域名卡住就全线停摆；
            //   4) 并发写 dnsEndPointAddress 是安全的：该字典是 ConcurrentDictionary，
            //      且每格只由一个任务写。
            var tasks = this.dnsEndPointAddress
                .OrderBy(item => item.Value.Length)
                .Select(keyValue => this.TestSpeedOneAsync(keyValue.Key, keyValue.Value, hostsOnly, cancellationToken))
                .ToArray();

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 对单个域名做解析测速，并记录结果
        /// </summary>
        private async Task TestSpeedOneAsync(
            DnsEndPoint dnsEndPoint,
            IPAddress[] oldAddresses,
            bool hostsOnly,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.speedSemaphore.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var newAddresses = await this.addressService.GetAddressesAsync(dnsEndPoint, oldAddresses, hostsOnly, cancellationToken);
                this.dnsEndPointAddress[dnsEndPoint] = newAddresses;

                var oldSegmentums = oldAddresses.Take(MAX_IP_COUNT);
                var newSegmentums = newAddresses.Take(MAX_IP_COUNT);
                if (oldSegmentums.SequenceEqual(newSegmentums) == false)
                {
                    var addressArray = string.Join(", ", newSegmentums.Select(item => item.ToString()));
                    this.logger.LogInformation($"{dnsEndPoint.Host}:{dnsEndPoint.Port}->[{addressArray}]");
                }
            }
            catch (OperationCanceledException)
            {
                // 停机取消属正常路径，静默处理
            }
            catch (Exception ex)
            {
                // 单个域名失败不能影响其余域名（串行版本下一个卡住就全线停摆）
                this.logger.LogWarning($"{dnsEndPoint.Host}:{dnsEndPoint.Port} 解析测速失败：{ex.Message}");
            }
            finally
            {
                this.speedSemaphore.Release();
            }
        }

        /// <summary>
        /// 刷新所有域名的IP（清空缓存并重新解析测速）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            this.logger.LogInformation("触发IP刷新：重新解析测速（保留探测缓存，避免失败时全量重探）");

            // 主动解除所有域名的IP拉黑状态。
            // 拉黑本身只在 BlacklistDuration 内自然过期，若不在刷新时清除，
            // 一次网络瞬断导致的集体拉黑会让该域名在拉黑期内完全不可用；
            // 而单域名冷却只有30秒，远短于2分钟拉黑时长，两者不配合就没有恢复路径。
            foreach (var endPoint in this.dnsEndPointAddress.Keys)
            {
                this.healthTracker.Reset(endPoint.Host);
            }

            // 【关键】失败恢复路径下刻意不清探测缓存（addressElapsedCache）。
            // 该缓存有 5 分钟有效期，清掉它会让下一轮测速对每个候选 IP 重新发起一次连接，
            // 而本方法正是由"连接失败"触发的——于是形成正反馈：
            //   连不上 → 清探测缓存 → 全量重探（数十次并发建连）→ 更像攻击流量 → 更容易被阻断 → 连不上
            // 保留缓存让重探只发生在真正过期时，故障恢复的额外建连量被压到最低。
            // 手动刷新（RefreshHostsAsync）语义不同，仍走完整清缓存。
            //
            // 但**解析结果必须失效**：DoH 正缓存有 10 分钟，若不清，重解析会原样拿回
            // 同一批刚被阻断的 IP，"恢复"只是把坏 IP 再排一遍，恢复速度永远快不起来。
            // 这里只清 DNS/DoH 的解析结果（换IP 的来源），不动探测缓存（避免探测风暴）。
            this.dnsClient.InvalidateCache();

            await this.TestSpeedAsync(hostsOnly: false, cancellationToken);
            await this.hostsService.RefreshAsync(cancellationToken);
        }

        /// <summary>
        /// 上报一次连接成功
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">IP</param>
        public void ReportSuccess(DnsEndPoint endPoint, IPAddress address)
        {
            this.healthTracker.ReportSuccess(endPoint.Host, address);
        }

        /// <summary>
        /// 上报一次连接失败
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">IP</param>
        public void ReportFailure(DnsEndPoint endPoint, IPAddress address)
        {
            this.healthTracker.ReportFailure(endPoint.Host, address);

            // 首选IP 已被拉黑时立即重选，避免后续请求继续排队等它的超时。
            // 每个域名REFRESH_COOLDOWN（10s）内最多触发一次，防止失败风暴把 DNS 打满。
            var blacklisted = this.healthTracker.IsBlacklisted(endPoint.Host, address);
            if (blacklisted == false)
            {
                return;
            }

            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) == false ||
                addresses.Length == 0)
            {
                return;
            }

            // 【恢复速度】只有"首选IP 被拉黑"这一种情况会刷新，条件过窄：
            // 若首选 IP 仍可用（尚未累计到阈值）而次选已连续失败拉黑，
            // 该域名实际上已只剩一个候选IP 且它是坏的，此时不会触发刷新，
            // 只能等 10分钟 DoH 缓存自然过期才可能换IP。
            // 放宽为：只要被拉黑的IP 仍占据候选列表前部（有新IP 可顶替）就触发重选。
            var topCount = Math.Min(MAX_IP_COUNT, addresses.Length);
            var occupiesTop = false;
            for (var i = 0; i < topCount; i++)
            {
                if (addresses[i].Equals(address))
                {
                    occupiesTop = true;
                    break;
                }
            }

            if (occupiesTop == false)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (this.refreshCooldown.TryGetValue(endPoint.Host, out var last) && now - last < REFRESH_COOLDOWN)
            {
                return;
            }
            this.refreshCooldown[endPoint.Host] = now;

            this.logger.LogInformation(
                $"{endPoint.Host} 的 {address} 已被判定不可用，立即触发IP更新以尽快恢复");

            // 观察异常：否则刷新失败会成为 unobserved task exception 而完全静默
            _ = this.RefreshAsync().ContinueWith(
                task => this.logger.LogError(task.Exception ?? new Exception("未知异常"), $"{endPoint.Host} 自动刷新IP失败"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// 该IP是否已被判定为不可用
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">IP</param>
        /// <returns></returns>
        public bool IsBlacklisted(DnsEndPoint endPoint, IPAddress address)
        {
            return this.healthTracker.IsBlacklisted(endPoint.Host, address);
        }

        /// <summary>
        /// 刷新所有域名的IP（仅使用在线hosts源，不发起DNS查询）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RefreshHostsAsync(CancellationToken cancellationToken = default)
        {
            this.logger.LogInformation("手动触发IP刷新：仅使用在线hosts源");
            this.addressService.ClearCache();

            // 【v2.6.6 P1-2】force=true：用户显式点击"刷新"，必须真的去拉一次源，
            // 不能被后台 5 分钟节流挡成"点了没反应"。
            await this.hostsService.RefreshAsync(cancellationToken, force: true);
            await this.TestSpeedAsync(hostsOnly: true, cancellationToken);
        }

        /// <summary>
        /// 获取IP健康度快照
        /// </summary>
        /// <param name="includeHealthy">是否包含健康条目</param>
        /// <param name="maxCount">最大条数</param>
        /// <returns></returns>
        public IReadOnlyList<IpHealthSnapshot> GetIpHealth(bool includeHealthy = true, int maxCount = 200)
        {
            return this.healthTracker.GetSnapshots(includeHealthy, maxCount);
        }
    }
}
