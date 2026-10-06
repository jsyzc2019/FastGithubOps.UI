using FastGithub.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP服务
    /// 域名IP关系缓存10分钟
    /// IPEndPoint时延缓存5分钟
    /// IPEndPoint连接超时10秒
    /// </summary>
    sealed class IPAddressService
    {
        private record DomainAddress(string Domain, IPAddress Address);
        private readonly TimeSpan domainAddressExpiration = TimeSpan.FromMinutes(10d);
        private readonly IMemoryCache domainAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private record AddressElapsed(IPAddress Address, TimeSpan Elapsed);
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);
        // 默认探测（TCP+TLS）预算。跨境链路 RTT 差异极大：github.com 交互请求常 ~5s，
        // 而 raw/codeload 等大文件域名单条响应曾达 12s+。默认值仅作兜底，
        // 具体域名可用 DomainConfig.ConnectTimeout 覆盖（见 appsettings.github.json）。
        private readonly TimeSpan defaultConnectTimeout = TimeSpan.FromSeconds(15d);
        private readonly IMemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly DnsClient dnsClient;
        private readonly HostsService hostsService;
        private readonly IpHealthTracker healthTracker;
        private readonly FastGithubConfig fastGithubConfig;

        /// <summary>
        /// IP服务
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="hostsService"></param>
        /// <param name="healthTracker"></param>
        public IPAddressService(DnsClient dnsClient, HostsService hostsService, IpHealthTracker healthTracker, FastGithubConfig fastGithubConfig)
        {
            this.dnsClient = dnsClient;
            this.hostsService = hostsService;
            this.healthTracker = healthTracker;
            this.fastGithubConfig = fastGithubConfig;
        }

        /// <summary>
        /// 清空IP缓存，强制后续重新解析与测速
        /// </summary>
        public void ClearCache()
        {
            (this.domainAddressCache as MemoryCache)?.Compact(1.0);
            (this.addressElapsedCache as MemoryCache)?.Compact(1.0);
        }

        /// <summary>
        /// 并行获取可连接的IP
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="oldAddresses"></param>
        /// <param name="hostsOnly">是否仅使用在线hosts源提供的IP（不发起DNS查询）</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress[]> GetAddressesAsync(DnsEndPoint dnsEndPoint, IEnumerable<IPAddress> oldAddresses, bool hostsOnly, CancellationToken cancellationToken)
        {
            var ipEndPoints = new HashSet<IPEndPoint>();

            // 历史未过期的IP节点（手动刷新、hosts源覆盖的域名均不保留历史DNS结果）
            if (hostsOnly == false && this.hostsService.IsCovered(dnsEndPoint.Host) == false)
            {
                foreach (var address in oldAddresses)
                {
                    var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                    if (this.domainAddressCache.TryGetValue(domainAddress, out _))
                    {
                        ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                    }
                }
            }

            // 新解析出的IP节点
            await foreach (var address in this.dnsClient.ResolveAsync(dnsEndPoint, fastSort: false, cancellationToken, hostsOnly))
            {
                ipEndPoints.Add(new IPEndPoint(address, dnsEndPoint.Port));
                var domainAddress = new DomainAddress(dnsEndPoint.Host, address);
                this.domainAddressCache.Set(domainAddress, default(object), this.domainAddressExpiration);
            }

            if (ipEndPoints.Count == 0)
            {
                return Array.Empty<IPAddress>();
            }

            var host = dnsEndPoint.Host;
            var addressElapsedTasks = ipEndPoints.Select(item => this.GetAddressElapsedAsync(item, host, cancellationToken));
            var addressElapseds = await Task.WhenAll(addressElapsedTasks);

            var connectable = addressElapseds
                .Where(item => item.Elapsed < TimeSpan.MaxValue)
                .ToArray();

            // 探测全部超时（多为"网络慢"而非"IP坏"，见上方 connectTimeout 已放宽到 10s）：
            // 不立即判死刑。把所有候选交回上层，由 live connect（同样 10s 预算）再试一次，
            // 否则一个慢网络瞬间就会变成"找不到任何可成功连接的IP"。
            if (connectable.Length == 0)
            {
                connectable = addressElapseds;
            }

            // 被拉黑的IP直接剔除；若全部被拉黑则退化为按健康度排序，保证不会无IP可用
            var healthy = connectable
                .Where(item => this.healthTracker.IsBlacklisted(host, item.Address) == false)
                .ToArray();

            var candidates = healthy.Length > 0 ? healthy : connectable;

            // 【v2.6.9】把候选池大小回填给健康度跟踪器。
            // 池大小决定"拉黑一个 IP 的代价"：池 ≥2 时拉黑一个只损失 1/2 冗余，
            // 池 =1 时拉黑等于**整个域名瞬时不可用**（实测 github.com 常态就是 1 个候选，
            // 因为 GitHub 权威 DNS 对它只返 1 条 A 记录）。
            // 必须让跟踪器知道这个数字，才能对单 IP 域名用更严的失败阈值与更短的拉黑时长。
            this.healthTracker.SetPoolSize(host, candidates.Length);

            // 健康度优先于时延：一个"握手快但连不通"的IP毫无价值（dev-sidecar 的 doRank 思路）
            return candidates
                .OrderBy(item => this.healthTracker.GetPenalty(host, item.Address))
                .ThenBy(item => item.Elapsed)
                .Select(item => item.Address)
                .ToArray();
        }


        /// <summary>
        /// 取当前域名生效的探测预算：优先按域名配置的 ConnectTimeout，否则用默认值。
        /// 与 HttpClientHandler 的建连预算保持一致，避免探测把"慢但可用"的IP判死。
        /// </summary>
        private TimeSpan GetConnectBudget(string host)
        {
            return this.fastGithubConfig.TryGetDomainConfig(host, out var domainConfig) && domainConfig?.ConnectTimeout != null
                ? domainConfig.ConnectTimeout.Value
                : this.defaultConnectTimeout;
        }

        /// <summary>
        /// 获取IP节点的时延（纯 TCP 握手探测）
        /// <para>
        /// 【为何这里只做 TCP 握手，不做 TLS 握手】
        /// 早期版本在探测阶段也完成一次真实 TLS 握手，想借此淘汰"TCP 通但 TLS 被阻断"的IP。
        /// 但本方法是**后台每秒轮询**的高频路径（见 DomainResolveHostedService.testPeriodTimeSpan），
        /// 每个候选IP 每次探测都会惊扰出一个完整 TLS 会话，随后立刻 Dispose。
        /// 几十个候选 IP 并发如此，在GitHub 边缘节点看来与端口扫描/攻击流量高度相似，
        /// 是「日志正常但反复被阻断」的直接成因之一——恰好违背了探测本想达到的目的。
        /// <para>
        /// TLS 层可用性交由真实请求路径判定：HttpClientHandler.ConnectAsync 在建连时本就做
        /// 完整 TLS 握手，且只在该IP真正被选中时才发生；配合 IpHealthTracker 的成败反馈，
        /// "握手快但用不了"的IP 会在少数几次真实请求后自然降权，无需在后台重复试探。
        /// 后台探测只负责给出轻量的相对时延，TCP 三次握手的开销与风控特征都远低于 TLS。
        /// </para>
        /// </summary>
        /// </summary> 
        /// <param name="endPoint"></param>
        /// <param name="host">域名</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedAsync(IPEndPoint endPoint, string host, CancellationToken cancellationToken)
        {
            // 已知被拉黑的 IP 直接跳过探测：否则每轮测速仍会对它发起一次 TCP(/TLS) 握手，
            // 对已确认不可用的目标持续产生无效连接，既浪费又增加被对端风控注意的概率。
            // 全部候选都被拉黑时，上层仍会回退到"强制试一轮"，恢复路径不受影响。
            if (this.healthTracker.IsBlacklisted(host, endPoint.Address))
            {
                return new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
            }

            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var addressElapsed))
            {
                return addressElapsed;
            }

            var connectBudget = this.GetConnectBudget(host);
            var stopWatch = Stopwatch.StartNew();
            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(connectBudget);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endPoint, linkedTokenSource.Token);

                addressElapsed = new AddressElapsed(endPoint.Address, stopWatch.Elapsed);
                return this.addressElapsedCache.Set(endPoint, addressElapsed, this.normalElapsedExpiration);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                addressElapsed = new AddressElapsed(endPoint.Address, TimeSpan.MaxValue);
                var expiration = IsLocalNetworkProblem(ex) ? this.problemElapsedExpiration : this.normalElapsedExpiration;
                return this.addressElapsedCache.Set(endPoint, addressElapsed, expiration);
            }
            finally
            {
                stopWatch.Stop();
            }
        }

        /// <summary>
        /// 是否为本机网络问题
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsLocalNetworkProblem(Exception ex)
        {
            if (ex is not SocketException socketException)
            {
                return false;
            }

            var code = socketException.SocketErrorCode;
            return code == SocketError.NetworkDown || code == SocketError.NetworkUnreachable;
        }
    }
}
