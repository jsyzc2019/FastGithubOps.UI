using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FastGithub.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// GitHub 官方 meta IP 段服务（api.github.com/meta）
    /// </summary>
    /// <remarks>
    /// 【v2.6.12 B1】生态调研新发现（见 docs/生态调研-可借鉴点-20261031.md）：
    /// GitHub 官方通过 meta API 公开 web/api/git 等 CIDR 段，且**已实测验证**
    /// github.com 的真实 IP（20.205.243.166、140.82.121.3/4 等）全部落在 web 段内。
    /// 这正好解决核心痛点：GitHub 权威 DNS 对 github.com 常态只回 1 条 A 记录，
    /// 候选池=1 意味着这个 IP 被阻断时整个域名瞬时不可用。
    /// 用官方段采样候选 → 交由现有 TCP 握手竞速 + IpHealthTracker 筛选，
    /// 单 IP 域名的候选池从 1 扩到 N。生态内其它项目（静态 hosts 系）均未利用此数据。
    /// <para>
    /// 拉取通道：HttpClient 的 ConnectCallback 复用 <see cref="DnsClient"/>（DoH 链）
    /// 解析 api.github.com，不走系统 DNS —— 明文 DNS 已被 RST，系统解析不可信。
    /// 数据缓存 24 小时；拉取失败保留旧段；本服务**绝不抛异常**（候选扩充是增益项，失败静默）。
    /// </para>
    /// </remarks>
    sealed class MetaIpRangesService
    {
        private const string MetaUrl = "https://api.github.com/meta";

        /// <summary>
        /// 段数据的缓存时长。官方段变化频率极低（月级），24h 足够。
        /// </summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24d);

        /// <summary>
        /// 单域名单轮最多追加的采样候选数。
        /// <para>取 6：足以把候选池从 1 扩到 7；配合握手竞速（每候选 ~1s 并行）
        /// 不会显著拖慢测速，也把探测流量控制在边缘节点不反感的量级。</para>
        /// </summary>
        private const int MaxSamplesPerDomain = 6;

        private readonly DnsClient dnsClient;
        private readonly ILogger<MetaIpRangesService> logger;
        private readonly IOptionsMonitor<FastGithubOptions> options;
        private readonly HttpClient httpClient;

        /// <summary>
        /// 已解析的官方段。key = "web" / "api" / "git"，value = 该类型的全部 IPv4 段。
        /// </summary>
        private volatile Dictionary<string, List<CidrSegment>>? ranges;

        private DateTime fetchedAt = DateTime.MinValue;

        /// <summary>
        /// 拉取互斥：防止并发重复拉取（single-flight）。
        /// </summary>
        private readonly SemaphoreSlim fetchGate = new(1, 1);

        public MetaIpRangesService(DnsClient dnsClient, IOptionsMonitor<FastGithubOptions> options, ILogger<MetaIpRangesService> logger)
        {
            this.dnsClient = dnsClient;
            this.options = options;
            this.logger = logger;
            this.httpClient = CreateHttpClient();
        }

        /// <summary>
        /// 为指定域名从官方段采样额外候选 IP；不可用/不适用/尚未拉取时返回空数组。
        /// <para>本方法同步返回（拉取在后台惰性进行），不抛异常，不影响主解析链路。</para>
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="currentCandidates">DNS/hosts 已给出的候选（采样会避开它们）</param>
        /// <returns>追加的采样候选（去重、限量）</returns>
        public IPAddress[] TrySampleCandidates(string host, IReadOnlyList<IPAddress> currentCandidates)
        {
            try
            {
                if (this.options.CurrentValue.MetaCandidateExpand == false)
                {
                    return Array.Empty<IPAddress>();
                }

                var rangeType = GetRangeType(host);
                if (rangeType == null)
                {
                    return Array.Empty<IPAddress>();
                }

                var snapshot = this.ranges;
                if (snapshot == null || DateTime.UtcNow - this.fetchedAt > CacheLifetime)
                {
                    // 惰性后台拉取：本轮先用现有数据（可能为空），不阻塞解析链路
                    _ = this.FetchAsyncSafe();
                    if (snapshot == null)
                    {
                        return Array.Empty<IPAddress>();
                    }
                }

                if (snapshot.TryGetValue(rangeType, out var segments) == false || segments.Count == 0)
                {
                    return Array.Empty<IPAddress>();
                }

                var existing = new HashSet<IPAddress>(currentCandidates);
                var seed = Environment.TickCount ^ StringComparer.OrdinalIgnoreCase.GetHashCode(host);
                var sampled = SampleFromSegments(segments, existing, MaxSamplesPerDomain, seed);

                if (sampled.Length > 0)
                {
                    this.logger.LogDebug(
                        $"{host}: 从官方 {rangeType} 段采样 {sampled.Length} 个候选" +
                        $"（原候选 {currentCandidates.Count} 个）");
                }
                return sampled;
            }
            catch (Exception ex)
            {
                // 候选扩充是增益项：任何异常都不能影响主解析链路
                this.logger.LogDebug($"meta 段采样失败（不影响解析）：{ex.Message}");
                return Array.Empty<IPAddress>();
            }
        }

        /// <summary>
        /// 判定域名适用的官方段类型；不适用返回 null。
        /// <para>api.github.com 用 "api" 段；github.com 系（含 codeload/gist/collector 等）
        /// 用 "web" 段。meta 不覆盖 *.githubusercontent.com（Fastly CDN），那些域名不适用。</para>
        /// </summary>
        internal static string? GetRangeType(string host)
        {
            if (string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return "api";
            }

            if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
            {
                return "web";
            }

            return null;
        }

        /// <summary>
        /// 从段集合采样候选：含现有候选的段排前（同段更可能是可达的入口集群），
        /// 其余段按 seed 轮转；跨段轮转保证多样性，确定性 seed 便于自检。
        /// </summary>
        /// <param name="segments">段集合</param>
        /// <param name="existing">已有候选（采样避开）</param>
        /// <param name="maxCount">采样上限</param>
        /// <param name="seed">随机种子（决定段内起始偏移）</param>
        /// <returns></returns>
        internal static IPAddress[] SampleFromSegments(List<CidrSegment> segments, HashSet<IPAddress> existing, int maxCount, int seed)
        {
            if (segments.Count == 0 || maxCount <= 0)
            {
                return Array.Empty<IPAddress>();
            }

            // 含现有候选的段排前，其余保持原序（稳定）
            var ordered = segments
                .Select((segment, index) => (segment, index))
                .OrderBy(pair => segmentContainsExisting(pair.segment, existing) ? 0 : 1)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.segment)
                .ToList();

            // 起始段随 seed 轮转，让不同域名/不同轮次覆盖不同段
            var offset = Math.Abs(seed) % ordered.Count;
            var result = new List<IPAddress>();
            var used = new HashSet<IPAddress>(existing);

            for (var i = 0; i < ordered.Count && result.Count < maxCount; i++)
            {
                var segment = ordered[(offset + i) % ordered.Count];
                foreach (var ip in SampleSegment(segment, used, seed + i))
                {
                    if (result.Count >= maxCount)
                    {
                        break;
                    }
                    if (used.Add(ip))
                    {
                        result.Add(ip);
                    }
                }
            }

            return result.ToArray();

            static bool segmentContainsExisting(CidrSegment segment, HashSet<IPAddress> existing)
            {
                foreach (var e in existing)
                {
                    if (segment.Contains(e))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// 从单个段内采样：取段内 1/6、1/2、5/6 三个基准位（按 seed 旋转），
        /// 跳过已用地址。段过小（/32 等）只取网络地址本身。
        /// </summary>
        private static IEnumerable<IPAddress> SampleSegment(CidrSegment segment, HashSet<IPAddress> used, int seed)
        {
            var size = segment.AddressCount;
            if (size <= 2)
            {
                var only = segment.GetAddress(0);
                if (used.Contains(only) == false)
                {
                    yield return only;
                }
                yield break;
            }

            var bases = new long[] { size / 6, size / 2, (size * 5) / 6 };
            var rotation = Math.Abs(seed) % 3;
            for (var i = 0; i < bases.Length; i++)
            {
                var index = (bases[(i + rotation) % bases.Length] + Math.Abs(seed) % 3) % size;
                var ip = segment.GetAddress((int)index);
                if (used.Contains(ip) == false)
                {
                    yield return ip;
                }
            }
        }

        /// <summary>
        /// 后台拉取官方段（single-flight；失败保留旧数据并按冷却期重试）。
        /// </summary>
        private async Task FetchAsyncSafe()
        {
            if (await this.fetchGate.WaitAsync(0) == false)
            {
                return; // 已有拉取在进行
            }

            try
            {
                // 失败后退避：1 小时内不重试（避免 meta 不可达时反复打）
                if (DateTime.UtcNow - this.fetchedAt < TimeSpan.FromHours(1d))
                {
                    return;
                }

                var json = await this.httpClient.GetStringAsync(MetaUrl, CancellationToken.None);
                var parsed = ParseRanges(json);
                var total = parsed.Values.Sum(list => list.Count);
                if (total == 0)
                {
                    this.logger.LogWarning("meta API 返回内容无法解析出任何 IPv4 段，保留原有数据");
                    return;
                }

                this.ranges = parsed;
                this.fetchedAt = DateTime.UtcNow;
                this.logger.LogInformation($"已更新 GitHub 官方 IP 段（web {parsed.GetValueOrDefault("web", new List<CidrSegment>()).Count} 段、" +
                    $"api {parsed.GetValueOrDefault("api", new List<CidrSegment>()).Count} 段、git {parsed.GetValueOrDefault("git", new List<CidrSegment>()).Count} 段），用于候选池扩充");
            }
            catch (Exception ex)
            {
                // 拉取失败静默：fetchedAt 未更新，1 小时后自然重试
                this.logger.LogDebug($"meta API 拉取失败（1 小时后重试）：{ex.Message}");
            }
            finally
            {
                this.fetchGate.Release();
            }
        }

        /// <summary>
        /// 解析 meta JSON 的指定类型段（web/api/git），仅保留可解析的 IPv4 CIDR。
        /// </summary>
        internal static Dictionary<string, List<CidrSegment>> ParseRanges(string json)
        {
            var result = new Dictionary<string, List<CidrSegment>>();
            try
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (doc == null)
                {
                    return result;
                }

                foreach (var type in new[] { "web", "api", "git" })
                {
                    if (doc.TryGetValue(type, out var element) && element.ValueKind == JsonValueKind.Array)
                    {
                        var segments = new List<CidrSegment>();
                        foreach (var item in element.EnumerateArray())
                        {
                            var cidr = item.GetString();
                            if (cidr != null && CidrSegment.TryParse(cidr, out var segment))
                            {
                                segments.Add(segment);
                            }
                        }
                        result[type] = segments;
                    }
                }
            }
            catch (JsonException)
            {
                // 畸形内容按空处理
            }
            return result;
        }

        /// <summary>
        /// 创建经自家 DoH 链解析的 HttpClient（不走系统 DNS）。
        /// </summary>
        private HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    await foreach (var address in this.dnsClient.ResolveAsync(context.DnsEndPoint, fastSort: true, cancellationToken))
                    {
                        try
                        {
                            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                            await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            // 换下一个候选
                        }
                    }
                    throw new InvalidOperationException($"无法为 meta API 建立连接：{context.DnsEndPoint.Host}");
                },
                ConnectTimeout = TimeSpan.FromSeconds(10d),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10d),
            };

            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30d) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FastGithubOps-UI");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>
        /// IPv4 CIDR 段
        /// </summary>
        internal readonly struct CidrSegment
        {
            private readonly uint network;
            private readonly int prefixLength;

            private CidrSegment(uint network, int prefixLength)
            {
                this.network = network;
                this.prefixLength = prefixLength;
            }

            /// <summary>
            /// 段内地址总数（含网络地址与广播地址）
            /// </summary>
            public long AddressCount => 1L << (32 - this.prefixLength);

            public static bool TryParse(string cidr, out CidrSegment segment)
            {
                segment = default;
                var parts = cidr.Split('/');
                if (parts.Length != 2 || int.TryParse(parts[1], out var prefix) == false)
                {
                    return false;
                }

                if (prefix < 8 || prefix > 32 || IPAddress.TryParse(parts[0], out var ip) == false)
                {
                    return false;
                }

                var bytes = ip.GetAddressBytes();
                if (bytes.Length != 4)
                {
                    return false; // 仅 IPv4
                }

                var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
                segment = new CidrSegment(value & mask, prefix);
                return true;
            }

            public bool Contains(IPAddress address)
            {
                var bytes = address.GetAddressBytes();
                if (bytes.Length != 4)
                {
                    return false;
                }

                var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
                var mask = this.prefixLength == 0 ? 0u : uint.MaxValue << (32 - this.prefixLength);
                return (value & mask) == this.network;
            }

            /// <summary>
            /// 取段内第 index 个地址（0 = 网络地址）
            /// </summary>
            public IPAddress GetAddress(int index)
            {
                var value = this.network + (uint)index;
                return new IPAddress(new[]
                {
                    (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
                });
            }
        }
    }
}
