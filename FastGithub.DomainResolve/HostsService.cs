using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 在线hosts源解析服务
    /// 定时从远程hosts地址拉取“域名->IP”映射，作为DNS解析之外的额外候选IP来源
    /// </summary>
    sealed class HostsService
    {
        private readonly HttpClient httpClient;
        private readonly IOptionsMonitor<FastGithubOptions> options;
        private readonly ILogger<HostsService> logger;
        private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> mapping = new();

        /// <summary>
        /// 连续失败次数，用于在源长期不可用时给出明确告警而非静默失效
        /// </summary>
        private int consecutiveFailures;

        /// <summary>
        /// 累计被丢弃的无效 IP 数量（环回/内网/污染段）。
        /// 源里出现这类地址说明源本身已被污染或格式异常，需明确告警而非静默丢弃——
        /// 否则表现只是"某些域名不通"，几乎无法定位。
        /// </summary>
        private int invalidIpCount;

        /// <summary>
        /// 在线hosts源解析服务
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        public HostsService(
            IOptionsMonitor<FastGithubOptions> options,
            ILogger<HostsService> logger)
        {
            this.options = options;
            this.logger = logger;
            this.httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10d) };
        }

        /// <summary>
        /// 刷新hosts映射，拉取远程hosts源并解析为“域名->IP”缓存
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            var hostsUrl = this.options.CurrentValue.HostsUrl?.Trim();
            if (string.IsNullOrEmpty(hostsUrl))
            {
                // 显式禁用在线hosts源，只依赖本机DNS解析
                if (this.mapping.IsEmpty == false)
                {
                    this.mapping.Clear();
                }
                return;
            }

            try
            {
                var content = await this.httpClient.GetStringAsync(hostsUrl, cancellationToken);
                var map = Parse(content, out var invalidCount);
                this.invalidIpCount = invalidCount;

                this.mapping.Clear();
                foreach (var item in map)
                {
                    this.mapping[item.Key] = item.Value;
                }

                this.consecutiveFailures = 0;
                this.logger.LogInformation($"已更新在线hosts解析，共{this.mapping.Count}个域名");

                if (this.invalidIpCount > 0)
                {
                    this.logger.LogWarning(
                        $"在线hosts源中有 {this.invalidIpCount} 个无效IP（127.0.0.1 等环回地址、" +
                        "私有网段或已知污染地址）已被丢弃。这类地址若被当作候选会把请求导向本机或错误目标。" +
                        "如持续出现，说明该源已被污染，建议更换（FastGithub:HostsUrl）。");
                }
            }
            catch (Exception ex)
            {
                this.consecutiveFailures++;
                this.logger.LogWarning($"在线hosts源更新失败（{hostsUrl}）：{ex.Message}");

                if (this.consecutiveFailures == 3)
                {
                    this.logger.LogError(
                        $"在线hosts源 {hostsUrl} 已连续 {this.consecutiveFailures} 次不可用。" +
                        "该地址返回的内容会被本程序直接当作域名->IP映射使用，" +
                        "请确认该域名仍由可信方长期维护；" +
                        "如需更换或禁用，可在 appsettings.json 中设置 FastGithub:HostsUrl。");
                }
            }
        }

        /// <summary>
        /// 获取指定域名在hosts源中的IP地址
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="addresses">命中的IP列表</param>
        /// <returns></returns>
        public bool TryGetAddresses(string host, out IReadOnlyList<IPAddress> addresses)
        {
            return this.mapping.TryGetValue(host, out addresses!);
        }

        /// <summary>
        /// 指定域名是否被hosts源覆盖
        /// </summary>
        /// <param name="host">域名</param>
        /// <returns></returns>
        public bool IsCovered(string host)
        {
            return this.mapping.TryGetValue(host, out var addresses) && addresses.Count > 0;
        }

        /// <summary>
        /// 解析hosts文本内容
        /// </summary>
        /// <param name="content">hosts文本</param>
        /// <param name="invalidCount">被丢弃的无效IP 数量（环回/内网/污染段）</param>
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> Parse(string content, out int invalidCount)
        {
            var map = new Dictionary<string, List<IPAddress>>();
            invalidCount = 0;
            foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || IPAddress.TryParse(parts[0], out var ip) == false)
                {
                    continue;
                }

                // 【关键】过滤环回/内网/保留段/已知污染地址。
                // 原实现只做 TryParse，导致源里出现的 127.0.0.1、192.168.x.x、
                // 或污染地址会被原样当作候选 IP，且hosts 源在 DnsClient 中优先级最高
                // （排在 DoH 与 DNS 之前），于是它会直接占据首选位置——
                // 实际日志已出现 "avatars.githubusercontent.com->127.0.0.1"，
                // 请求被导向本机回环，表现为"域名完全不通"且原因极难定位。
                if (IpAddressFilter.IsInvalid(ip))
                {
                    invalidCount++;
                    continue;
                }

                var host = parts[1].Trim().ToLowerInvariant();
                if (map.TryGetValue(host, out var list) == false)
                {
                    list = new List<IPAddress>();
                    map[host] = list;
                }
                if (list.Contains(ip) == false)
                {
                    list.Add(ip);
                }
            }

            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }
    }
}