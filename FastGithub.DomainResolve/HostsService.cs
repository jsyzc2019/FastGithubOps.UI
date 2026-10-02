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
                var map = Parse(content);

                this.mapping.Clear();
                foreach (var item in map)
                {
                    this.mapping[item.Key] = item.Value;
                }

                this.consecutiveFailures = 0;
                this.logger.LogInformation($"已更新在线hosts解析，共{this.mapping.Count}个域名");
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
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> Parse(string content)
        {
            var map = new Dictionary<string, List<IPAddress>>();
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

                var host = parts[1].Trim().ToLowerInvariant();
                if (map.TryGetValue(host, out var list) == false)
                {
                    list = new List<IPAddress>();
                    map[host] = list;
                }
                list.Add(ip);
            }
            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }
    }
}