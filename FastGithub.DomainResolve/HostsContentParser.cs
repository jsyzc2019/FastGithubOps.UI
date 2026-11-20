using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 解析社区 hosts 源并过滤无效 IP。
    /// </summary>
    public static class HostsContentParser
    {
        /// <summary>
        /// 从单个 hosts 源的内容中提取指定域名的有效 IP。
        /// </summary>
        public static IReadOnlyList<IPAddress> ParseAddresses(string content, string host)
        {
            var map = Parse(content, out _);
            return map.TryGetValue(host.ToLowerInvariant(), out var addresses) ? addresses : Array.Empty<IPAddress>();
        }

        /// <summary>
        /// 解析hosts源内容，自动识别 JSON 数组与 hosts 文本两种格式
        /// </summary>
        /// <remarks>
        /// 【v2.6.11 借鉴上游 v2.3.2】上游默认源已切换为
        /// https://raw.hellogithub.com/hosts.json（[["ip","domain"],...] 数组格式）。
        /// 本实现此前只解析文本格式：一旦源格式迁移，hosts 通道会静默失效
        /// （有"失败保留旧映射"保护不至于清空，但通道就此报废）。
        /// 识别规则与上游一致：内容以 '[' 或 '{' 开头按 JSON 解析，否则按文本解析。
        /// </remarks>
        /// <param name="content">hosts源内容</param>
        /// <param name="invalidCount">被丢弃的无效IP 数量（环回/内网/污染段）</param>
        /// <returns>域名->IP映射</returns>
        internal static Dictionary<string, IReadOnlyList<IPAddress>> Parse(string content, out int invalidCount)
        {
            var trimmed = content.TrimStart('\uFEFF').TrimStart();
            return trimmed.StartsWith('[') || trimmed.StartsWith('{')
                ? ParseJson(trimmed, out invalidCount)
                : ParseText(trimmed, out invalidCount);
        }

        /// <summary>
        /// 解析 JSON 格式：[["ip","domain"],...]
        /// </summary>
        /// <param name="content"></param>
        /// <param name="invalidCount">被丢弃的无效IP数量</param>
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> ParseJson(string content, out int invalidCount)
        {
            invalidCount = 0;
            var map = new Dictionary<string, List<IPAddress>>();
            try
            {
                var items = JsonSerializer.Deserialize<string[][]>(content);
                if (items == null)
                {
                    return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
                }

                foreach (var item in items)
                {
                    if (item == null || item.Length < 2 || string.IsNullOrWhiteSpace(item[1]))
                    {
                        continue;
                    }

                    if (IPAddress.TryParse(item[0], out var ip) == false)
                    {
                        continue;
                    }

                    // 与文本格式共用同一套无效IP过滤：JSON 源同样可能被投毒，
                    // 环回/内网/污染地址一旦入选会把请求导向本机或错误目标。
                    if (IpAddressFilter.IsInvalid(ip))
                    {
                        invalidCount++;
                        continue;
                    }

                    var host = item[1].Trim().ToLowerInvariant();
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
            }
            catch (JsonException)
            {
                // 解析失败按"空映射"处理：调用方保留旧映射并告警，不会清空已有数据。
            }
            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }

        /// <summary>
        /// 解析hosts文本内容
        /// </summary>
        /// <param name="content">hosts文本</param>
        /// <param name="invalidCount">被丢弃的无效IP 数量（环回/内网/污染段）</param>
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> ParseText(string content, out int invalidCount)
        {
            var map = new Dictionary<string, List<IPAddress>>();
            invalidCount = 0;
            foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var commentIndex = line.IndexOf('#');
                var hostsLine = commentIndex < 0 ? line : line[..commentIndex];
                var parts = hostsLine.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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

                foreach (var alias in parts.AsSpan(1))
                {
                    var host = alias.Trim().ToLowerInvariant();
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
            }

            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }
    }
}
