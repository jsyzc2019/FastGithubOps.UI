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
        /// </summary>
        private static readonly TimeSpan REFRESH_COOLDOWN = TimeSpan.FromSeconds(30d);

        private readonly ConcurrentDictionary<string, DateTime> refreshCooldown = new();
        private readonly DnsClient dnsClient;
        private readonly PersistenceService persistence;
        private readonly IPAddressService addressService;
        private readonly HostsService hostsService;
        private readonly IpHealthTracker healthTracker;
        private readonly ILogger<DomainResolver> logger;
        private readonly ConcurrentDictionary<DnsEndPoint, IPAddress[]> dnsEndPointAddress = new();

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

                await foreach (var adddress in this.dnsClient.ResolveAsync(endPoint, fastSort: true, cancellationToken))
                {
                    yield return adddress;
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
            foreach (var keyValue in this.dnsEndPointAddress.OrderBy(item => item.Value.Length))
            {
                var dnsEndPoint = keyValue.Key;
                var oldAddresses = keyValue.Value;

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
        }

        /// <summary>
        /// 刷新所有域名的IP（清空缓存并重新解析测速）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            this.logger.LogInformation("触发IP刷新：清空缓存并重新解析测速");
            this.addressService.ClearCache();
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

            // 首选IP已被拉黑时立即重选，避免后续请求继续排队等它的超时；
            // 每个域名30秒内最多触发一次，防止失败风暴把DNS打满
            if (this.healthTracker.IsBlacklisted(endPoint.Host, address) == false)
            {
                return;
            }

            if (this.dnsEndPointAddress.TryGetValue(endPoint, out var addresses) == false ||
                addresses.Length == 0 ||
                addresses[0].Equals(address) == false)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (this.refreshCooldown.TryGetValue(endPoint.Host, out var last) && now - last < REFRESH_COOLDOWN)
            {
                return;
            }
            this.refreshCooldown[endPoint.Host] = now;
            _ = this.RefreshAsync();
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
            await this.hostsService.RefreshAsync(cancellationToken);
            await this.TestSpeedAsync(hostsOnly: true, cancellationToken);
        }
    }
}
