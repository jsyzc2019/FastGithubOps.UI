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
        /// 对单个记录类型发起 wire 格式 DoH 查询。
        /// <para>
        /// 端点分两组：**国内优先**（阿里 223.5.5.5 / 腾讯 119.29.29.29，本机网络几乎必然可达）；
        /// **国际兜底**（Cloudflare / Google，部分网络下被阻断）。先试国内，国内任一端点成功即停止，
        /// 不再打扰国际；只有国内全失败时再试国际。所有 DoH 服务器对同域返回相同答案，
        /// "命中即止"既快又稳，也避免被阻断的国际端点拖累整体耗时。
        /// 每组内每个端点还有 1 次重试（见 QueryEndpointWithRetryAsync），应对瞬时超时。
        /// </para>
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

            var domestic = Endpoints.Where(e => e.Contains("223.5.5.5") || e.Contains("119.29.29.29")).ToArray();
            var international = Endpoints.Except(domestic).ToArray();

            var merged = new HashSet<IPAddress>();
            await QueryEndpointsAsync(domestic, dnsParam, merged, cancellationToken);
            if (merged.Count == 0)
            {
                await QueryEndpointsAsync(international, dnsParam, merged, cancellationToken);
            }
            return merged.ToArray();
        }

        /// <summary>
        /// 依次查询一组 DoH 端点，首个成功者即停止（命中即止），结果并入 sink。
        /// </summary>
        private async Task QueryEndpointsAsync(string[] endpoints, string dnsParam, HashSet<IPAddress> sink, CancellationToken cancellationToken)
        {
            foreach (var endpoint in endpoints)
            {
                var addresses = await QueryEndpointWithRetryAsync(endpoint, dnsParam, cancellationToken);
                foreach (var address in addresses)
                {
                    sink.Add(address);
                }
                if (sink.Count > 0)
                {
                    break;
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
                    return result;
                }
                if (attempt < maxTries - 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400d), cancellationToken);
                }
            }
            return Array.Empty<IPAddress>();
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
