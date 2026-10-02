using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP服务
    /// 域名IP关系缓存10分钟
    /// IPEndPoint时延缓存5分钟
    /// IPEndPoint连接超时5秒
    /// </summary>
    sealed class IPAddressService
    {
        private record DomainAddress(string Domain, IPAddress Address);
        private readonly TimeSpan domainAddressExpiration = TimeSpan.FromMinutes(10d);
        private readonly IMemoryCache domainAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private record AddressElapsed(IPAddress Address, TimeSpan Elapsed);
        private readonly TimeSpan problemElapsedExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan normalElapsedExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);

        // TLS 探测的独立预算：TCP 已连通时握手慢更可能是链路慢而非 IP 坏，
        // 与 TCP 超时共用一份预算会把"慢但可用"的 IP 误杀。
        private readonly TimeSpan tlsProbeTimeout = TimeSpan.FromSeconds(8d);
        private readonly IMemoryCache addressElapsedCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly DnsClient dnsClient;
        private readonly HostsService hostsService;
        private readonly IpHealthTracker healthTracker;

        /// <summary>
        /// IP服务
        /// </summary>
        /// <param name="dnsClient"></param>
        /// <param name="hostsService"></param>
        /// <param name="healthTracker"></param>
        public IPAddressService(DnsClient dnsClient, HostsService hostsService, IpHealthTracker healthTracker)
        {
            this.dnsClient = dnsClient;
            this.hostsService = hostsService;
            this.healthTracker = healthTracker;
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

            if (connectable.Length == 0)
            {
                return Array.Empty<IPAddress>();
            }

            // 被拉黑的IP直接剔除；若全部被拉黑则退化为按健康度排序，保证不会无IP可用
            var healthy = connectable
                .Where(item => this.healthTracker.IsBlacklisted(host, item.Address) == false)
                .ToArray();

            var candidates = healthy.Length > 0 ? healthy : connectable;

            // 健康度优先于时延：一个"握手快但连不通"的IP毫无价值（dev-sidecar 的 doRank 思路）
            return candidates
                .OrderBy(item => this.healthTracker.GetPenalty(host, item.Address))
                .ThenBy(item => item.Elapsed)
                .Select(item => item.Address)
                .ToArray();
        }


        /// <summary>
        /// 获取IP节点的时延
        /// <para>
        /// 对 https 端口额外完成一次真实 TLS 握手。只测 TCP 会选出"握手快但用不了"的 IP：
        /// 干扰设备对 TLS 层的阻断（RST/黑洞）在 TCP 层表现为连接成功，
        /// 于是这类 IP 靠极低时延长期占据首选，用户体感就是"明明连上了却一直转圈"。
        /// </para>
        /// </summary> 
        /// <param name="endPoint"></param>
        /// <param name="host">域名，用于TLS握手的SNI</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<AddressElapsed> GetAddressElapsedAsync(IPEndPoint endPoint, string host, CancellationToken cancellationToken)
        {
            if (this.addressElapsedCache.TryGetValue<AddressElapsed>(endPoint, out var addressElapsed))
            {
                return addressElapsed;
            }

            var stopWatch = Stopwatch.StartNew();
            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                using var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endPoint, linkedTokenSource.Token);

                if (IsTlsProbePort(endPoint.Port))
                {
                    // 握手预算独立于TCP：TCP已连通时握手慢更可能是链路慢而非IP坏。
                    // 这里不校验信任链——目的是确认"该IP能否完成TLS握手"，
                    // 真正的证书校验发生在请求连接时（HttpClientHandler）。
                    using var sslTimeoutSource = new CancellationTokenSource(this.tlsProbeTimeout);
                    using var sslTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sslTimeoutSource.Token);
                    using var sslStream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: true);
                    await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true
                    }, sslTokenSource.Token);
                }

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
        /// 该端口是否需要做TLS握手探测
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        private static bool IsTlsProbePort(int port) => port == 443 || port == 8443;

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
