using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        /// 正查询缓存时长：成功的 DoH 结果会被复用，避免后台每秒一次的测速把 DoH 打爆
        /// </summary>
        private static readonly TimeSpan positiveCacheTtl = TimeSpan.FromMinutes(2d);

        /// <summary>
        /// 单次 DoH 请求的整体预算
        /// </summary>
        private static readonly TimeSpan perRequestTimeout = TimeSpan.FromSeconds(8d);

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

        private readonly ILogger<DohResolver> logger;
        private readonly HttpClient httpClient;
        private readonly ConcurrentDictionary<string, (DateTime Expires, IReadOnlyList<IPAddress> Addresses)> positiveCache = new();

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
        /// 对单个记录类型发起 wire 格式 DoH 查询，并在所有端点间取并集
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> ResolveWireAsync(string host, ushort type, CancellationToken cancellationToken)
        {
            var requestBytes = BuildRequest(host, type);
            var dnsParam = ToBase64Url(requestBytes);

            var tasks = Endpoints.Select(endpoint => QueryEndpointAsync(endpoint, dnsParam, cancellationToken)).ToArray();
            var results = await Task.WhenAll(tasks);

            var merged = new HashSet<IPAddress>();
            foreach (var addresses in results)
            {
                foreach (var address in addresses)
                {
                    merged.Add(address);
                }
            }
            return merged.ToArray();
        }

        /// <summary>
        /// 向单个 DoH 端点发送 wire 格式查询并解析响应
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> QueryEndpointAsync(string endpoint, string dnsParam, CancellationToken cancellationToken)
        {
            try
            {
                var url = $"{endpoint}?dns={dnsParam}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("accept", "application/dns-message");
                using var response = await this.httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode == false)
                {
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
        }

        /// <summary>
        /// 构造一个最简 DNS 查询报文（RD=1，单问题）
        /// </summary>
        private static byte[] BuildRequest(string host, ushort type)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new System.IO.BinaryWriter(ms);

            // Header: ID(2) + Flags(2)=0x0100(RD) + QDCOUNT(2)=1 + 其余为0
            writer.Write((ushort)0x0000);
            writer.Write((ushort)0x0100);
            writer.Write((ushort)0x0001);
            writer.Write((ushort)0x0000);
            writer.Write((ushort)0x0000);
            writer.Write((ushort)0x0000);

            // QNAME: 长度前缀标签序列，以 0 结尾
            foreach (var label in host.Split('.'))
            {
                var labelBytes = Encoding.ASCII.GetBytes(label);
                writer.Write((byte)labelBytes.Length);
                writer.Write(labelBytes);
            }
            writer.Write((byte)0x00);

            // QTYPE + QCLASS(IN=1)
            writer.Write(type);
            writer.Write((ushort)0x0001);

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
