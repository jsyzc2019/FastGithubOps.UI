using DNS.Client;
using DNS.Client.RequestResolver;
using DNS.Protocol;
using DNS.Protocol.ResourceRecords;
using FastGithub.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// DNS客户端
    /// </summary>
    sealed class DnsClient
    {
        private const int DNS_PORT = 53;
        private const string LOCALHOST = "localhost";

        /// <summary>
        /// 解析兜底种子表：仅在 hosts源 + DoH + 明文DNS 全部返回空时生效。
        /// <para>
        /// 已知 GitHub 域名若因 DoH 全端点失败、明文DNS被RST而拿不到任何IP，连接层会直接抛
        /// "未能解析到任何可用ip" 让该域名彻底不可用（日志中 raw.githubusercontent.com 即此情况）。
        /// 此处回退到内置的真实、长期稳定的 GitHub/Fastly 边缘IP，保证始终有候选可竞速；
        /// 种子IP会照常经过 TCP+TLS 实测与健康度跟踪，DoH 恢复后新解析出的IP自然优先。
        /// 这些地址为公开稳定的 CDN 边缘IP，不会引入额外安全风险。
        /// </para>
        /// </summary>
        private static readonly Dictionary<string, IPAddress[]> SeedAddresses = new(StringComparer.OrdinalIgnoreCase)
        {
            ["github.com"] = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("20.26.156.215"), IPAddress.Parse("140.82.121.3") },
            ["api.github.com"] = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("140.82.121.3"), IPAddress.Parse("140.82.113.3") },
            ["gist.github.com"] = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("140.82.121.3") },
            ["collector.github.com"] = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("140.82.121.3") },
            ["codeload.github.com"] = new[] { IPAddress.Parse("20.205.243.166"), IPAddress.Parse("140.82.121.3") },
            ["raw.githubusercontent.com"] = new[] { IPAddress.Parse("185.199.108.133"), IPAddress.Parse("185.199.109.133"), IPAddress.Parse("185.199.110.133"), IPAddress.Parse("185.199.111.133") },
            ["avatars.githubusercontent.com"] = new[] { IPAddress.Parse("185.199.108.133"), IPAddress.Parse("185.199.109.133"), IPAddress.Parse("185.199.110.133"), IPAddress.Parse("185.199.111.133") },
            ["objects.githubusercontent.com"] = new[] { IPAddress.Parse("185.199.108.133"), IPAddress.Parse("185.199.109.133"), IPAddress.Parse("185.199.110.133"), IPAddress.Parse("185.199.111.133") },
        };

        private readonly DnscryptProxy dnscryptProxy;
        private readonly FastGithubConfig fastGithubConfig;
        private readonly HostsService hostsService;
        private readonly DohResolver dohResolver;
        private readonly ILogger<DnsClient> logger;

        private readonly ConcurrentDictionary<string, SemaphoreSlim> semaphoreSlims = new();
        private readonly IMemoryCache dnsStateCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        private readonly IMemoryCache dnsLookupCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly TimeSpan stateExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan minTimeToLive = TimeSpan.FromSeconds(30d);
        private readonly TimeSpan maxTimeToLive = TimeSpan.FromMinutes(10d);

        private readonly int resolveTimeout = (int)TimeSpan.FromSeconds(4d).TotalMilliseconds;
        private static readonly TimeSpan tcpConnectTimeout = TimeSpan.FromSeconds(2d);

        /// <summary>
        /// 明文 DNS "假可达"的冷却时长。TCP 通但查询被 RST 属网络环境特性，
        /// 短时间内无需再试；取 10 分钟与 DoH 正缓存同量级，也与端点熔断周期一致。
        /// </summary>
        private static readonly TimeSpan plainDnsBrokenCooldown = TimeSpan.FromMinutes(10d);

        /// <summary>
        /// 记录 (dns, 域名) 维度上"TCP 可达但查询被对端截断"，值为冷却截止时刻。
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTime> plainDnsBroken = new();

        private record LookupResult(IList<IPAddress> Addresses, TimeSpan TimeToLive);

        /// <summary>
        /// DNS客户端
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public DnsClient(
            DnscryptProxy dnscryptProxy,
            FastGithubConfig fastGithubConfig,
            HostsService hostsService,
            DohResolver dohResolver,
            ILogger<DnsClient> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.fastGithubConfig = fastGithubConfig;
            this.hostsService = hostsService;
            this.dohResolver = dohResolver;
            this.logger = logger;
        }

        /// <summary>
        /// 使解析结果缓存失效，强制下次重新解析。
        /// <para>
        /// 用于「IP 被阻断后的恢复」场景：DoH 正缓存长达 10 分钟，若不失效，
        /// 失败后触发的重解析会原样拿回同一批刚被阻断的 IP，恢复只是把坏 IP 再排一遍。
        /// 只清解析结果缓存（IP 来源），不动 IPAddressService 的探测缓存——
        /// 后者若一并清掉，会让每次故障都触发一轮全量重探（探测风暴）。
        /// </para>
        /// </summary>
        public void InvalidateCache()
        {
            (this.dnsLookupCache as MemoryCache)?.Compact(1.0);

            // DoH 有独立的正缓存（10 分钟），必须一并失效。
            // 漏掉这一句会导致"恢复"完全失效：重解析仍拿回同一批被阻断的 IP。
            this.dohResolver.InvalidateCache();
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="endPoint">远程结节</param>
        /// <param name="fastSort">是否使用快速排序</param>
        /// <param name="cancellationToken"></param>
        /// <param name="hostsOnly">是否仅使用在线hosts源（跳过DNS查询）</param>
        /// <returns></returns>
        public async IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, bool fastSort, [EnumeratorCancellation] CancellationToken cancellationToken, bool hostsOnly = false)
        {
            var hashSet = new HashSet<IPAddress>();

            // 在线hosts源提供的IP优先返回（它们经过源站筛选，质量最可靠）。
            // 原实现在这里直接 yield break，导致被hosts源覆盖的域名永远只有
            // 源里那一个候选IP——它一旦被干扰或故障，连接层没有任何备选可退，
            // "并发赛马"与"健康度排序"全都失效，只剩单点。
            // 改为继续用DNS结果补充：DNS污染段已由 IpAddressFilter 过滤，
            // 且所有候选都要过 TCP+TLS 探测才会被真正使用，混入不会引入坏IP。
            if (this.hostsService.TryGetAddresses(endPoint.Host, out var hostsAddresses) && hostsAddresses.Count > 0)
            {
                foreach (var address in hostsAddresses)
                {
                    if (hashSet.Add(address) == true)
                    {
                        yield return address;
                    }
                }

                // 手动刷新仅使用在线hosts源，未覆盖的域名不发起DNS查询
                if (hostsOnly == true)
                {
                    yield break;
                }
            }
            else if (hostsOnly == true)
            {
                yield break;
            }

            // DoH 解析：明文 53 端口 DNS 在部分网络下会被 RST 注入而彻底失效，
            // 这是此前「拿不到任何新 IP、只剩过期持久化 IP」的根因。
            // DoH 走 443 且使用 IP 字面量直连，规避该故障，作为候选 IP 的主要补充来源。
            // 仅正常解析路径使用；手动「仅 hosts 源刷新」语义上属于纯 hosts，跳过 DoH。
            var dohAddresses = await this.dohResolver.ResolveAsync(endPoint.Host, cancellationToken);
            foreach (var address in dohAddresses)
            {
                if (hashSet.Add(address) == true)
                {
                    yield return address;
                }
            }

            await foreach (var dns in this.GetDnsServersAsync(cancellationToken))
            {
                // 已知"TCP 通但查询被 RST"的明文 DNS：本轮直接跳过，
                // 不让它在每个域名上白等 resolveTimeout × 2（A + AAAA）。
                if (dns.Port == DNS_PORT && this.ShouldSkipPlainDns(dns, endPoint.Host))
                {
                    continue;
                }

                var addresses = await this.LookupAsync(dns, endPoint, fastSort, cancellationToken);
                foreach (var address in addresses)
                {
                    if (hashSet.Add(address) == true)
                    {
                        yield return address;
                    }
                }
            }

            // 解析兜底：hosts源 + DoH + 明文DNS 全部返回空时，对已知 GitHub 域名回退到内置种子IP，
            // 保证连接层永远有候选可竞速，避免 "未能解析到任何可用ip" 让域名彻底不可用。
            if (hashSet.Count == 0 && SeedAddresses.TryGetValue(endPoint.Host, out var seeds))
            {
                foreach (var address in seeds)
                {
                    if (hashSet.Add(address) == true)
                    {
                        yield return address;
                    }
                }
            }
        }

        /// <summary>
        /// 获取dns服务
        /// </summary>
        /// <returns></returns>
        private async IAsyncEnumerable<IPEndPoint> GetDnsServersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var cryptDns = this.dnscryptProxy.LocalEndPoint;
            if (cryptDns != null)
            {
                // 原实现重复yield两次，但解析结果按 {dns}/{域名} 缓存，
                // 第二次必然命中同一份缓存，属于无效代码，这里去掉
                yield return cryptDns;
            }

            foreach (var dns in this.fastGithubConfig.FallbackDns)
            {
                if (await this.IsDnsAvailableAsync(dns, cancellationToken))
                {
                    yield return dns;
                }
            }
        }

        /// <summary>
        /// 判断某个明文 DNS 是否已被证明「连得上但不响应查询」，据此跳过它。
        /// <para>
        /// 【关键背景】本机网络下明文 53 端口 DNS 是**典型的"假可达"**：
        /// TCP 握手能成功（<see cref="IsDnsAvailableAsync"/> 因此判定可用），
        /// 但发出查询后立刻被 RST（"远程主机强迫关闭了一个现有的连接"）。
        /// 结果是每个域名都要在这条死路上白等 <see cref="resolveTimeout"/>（4s），
        /// A + AAAA 两轮查询各自等满，单域名凭空多出 8s；
        /// 而这8s 直接吃掉上层请求 25s 预算的一大块，是启动期 504 的帮凶之一。
        /// <para>
        /// 这里按 dns 服务器维度记住"TCP 通但查询失败"，一旦确认就在冷却期内跳过它，
        /// 既省掉无谓的等待，也避免把被污染的 IP 混进候选池。
        /// <para>
        /// 【v2.6.2 修正】原实现按 (dns, 域名) 维度记录，等于**每个域名都要重新踩一次**：
        /// 实测 v2.6.2 日志中 223.5.5.5 与 119.29.29.29 各被 RST 14 次，覆盖 14 个不同域名，
        /// 熔断从未对第 2 个域名生效。因为"被 RST"是**该 DNS 服务器到本机链路**的属性，
        /// 与问的是哪个域名无关，故改为按 dns 维度记录：第一个域名确认后，
        /// 后续所有域名直接跳过，14 次重试降为 1 次。
        /// </para>
        /// 注意这只跳过**明文 DNS 查询**，DoH（443）完全不受影响。
        /// </para>
        /// </summary>
        private bool ShouldSkipPlainDns(IPEndPoint dns, string host)
        {
            var key = dns.ToString();
            if (this.plainDnsBroken.TryGetValue(key, out var until))
            {
                if (until > DateTime.UtcNow)
                {
                    return true;
                }
                this.plainDnsBroken.TryRemove(key, out _);
            }
            return false;
        }

        /// <summary>
        /// 标记某明文 DNS 服务器的查询被对端 RST/截断，进入冷却
        /// </summary>
        private void ReportPlainDnsBroken(IPEndPoint dns, string host, bool broken)
        {
            if (broken == false)
            {
                return;
            }

            var key = dns.ToString();
            if (this.plainDnsBroken.TryGetValue(key, out var until) && until > DateTime.UtcNow)
            {
                // 已在冷却中，不延长剩余时长
                return;
            }

            this.plainDnsBroken[key] = DateTime.UtcNow.Add(plainDnsBrokenCooldown);
        }

        /// <summary>
        /// 获取dns是否可用
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async ValueTask<bool> IsDnsAvailableAsync(IPEndPoint dns, CancellationToken cancellationToken)
        {
            if (dns.Port != DNS_PORT)
            {
                return true;
            }

            if (this.dnsStateCache.TryGetValue<bool>(dns, out var available))
            {
                return available;
            }

            var key = dns.ToString();
            var semaphore = this.semaphoreSlims.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(CancellationToken.None);

            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(tcpConnectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, cancellationToken);
                using var socket = new Socket(dns.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(dns, linkedTokenSource.Token);
                return this.dnsStateCache.Set(dns, true, this.stateExpiration);
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return this.dnsStateCache.Set(dns, false, this.stateExpiration);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="endPoint"></param>
        /// <param name="fastSort"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IList<IPAddress>> LookupAsync(IPEndPoint dns, DnsEndPoint endPoint, bool fastSort, CancellationToken cancellationToken = default)
        {
            var key = $"{dns}/{endPoint}";
            var semaphore = this.semaphoreSlims.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(CancellationToken.None);

            try
            {
                if (this.dnsLookupCache.TryGetValue<IList<IPAddress>>(key, out var value))
                {
                    return value;
                }
                var result = await this.LookupCoreAsync(dns, endPoint, fastSort, cancellationToken);
                return this.dnsLookupCache.Set(key, result.Addresses, result.TimeToLive);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<IPAddress>();
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"{endPoint.Host}@{dns}->{ex.Message}");

                // 区分"这次查询失败"与"这个 DNS 根本不可用"：
                // TCP 握手此前已成功（IsDnsAvailableAsync 判定可用），却在这一步被 RST /
                // 意外断流，属于该网络下明文 53 端口的典型封锁特征，值得记住并跳过后续查询；
                // 而 OperationCanceledException（超时取消）只是慢，不应判定为被封锁。
                if (dns.Port == DNS_PORT && IsBlockedByPeer(ex))
                {
                    this.ReportPlainDnsBroken(dns, endPoint.Host, broken: true);
                }

                var expiration = IsSocketException(ex) ? this.maxTimeToLive : this.minTimeToLive;
                return this.dnsLookupCache.Set(key, Array.Empty<IPAddress>(), expiration);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 是否为Socket异常
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsSocketException(Exception ex)
        {
            if (ex is SocketException)
            {
                return true;
            }

            var inner = ex.InnerException;
            return inner != null && IsSocketException(inner);
        }

        /// <summary>
        /// 判断异常是否为"对端主动截断连接"（RST / 意外断流）而非本地超时。
        /// <para>
        /// 这类异常说明链路本身通（能收到 TCP RST 或半包后被断开），
        /// 只是明文 DNS 查询不被放行；与"连不上"（超时）和"慢"（取消）是三种不同故障，
        /// 只有第一种适合进入熔断冷却。
        /// </para>
        /// </summary>
        private static bool IsBlockedByPeer(Exception ex)
        {
            // 超时取消属于"慢"，不判定为被封锁
            if (ex is OperationCanceledException || ex is TimeoutException)
            {
                return false;
            }

            if (ex is SocketException socketException)
            {
                var code = socketException.SocketErrorCode;
                return code == SocketError.ConnectionReset
                    || code == SocketError.ConnectionAborted
                    || code == SocketError.Shutdown;
            }

            // IOException 家族："Unable to read data from the transport connection" /
            // "Unexpected end of stream" 等，均为连接被中途截断的表现。
            // 注意这类异常常被包在 AggregateException / TargetInvocationException 里，需递归下钻。
            if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                return aggregate.InnerExceptions.Any(IsBlockedByPeer);
            }

            if (ex is IOException)
            {
                return true;
            }

            var inner = ex.InnerException;
            return inner != null && IsBlockedByPeer(inner);
        }


        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="endPoint"></param>
        /// <param name="fastSort"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<LookupResult> LookupCoreAsync(IPEndPoint dns, DnsEndPoint endPoint, bool fastSort, CancellationToken cancellationToken = default)
        {
            if (endPoint.Host == LOCALHOST)
            {
                var loopbacks = new List<IPAddress>();
                if (Socket.OSSupportsIPv4 == true)
                {
                    loopbacks.Add(IPAddress.Loopback);
                }
                if (Socket.OSSupportsIPv6 == true)
                {
                    loopbacks.Add(IPAddress.IPv6Loopback);
                }
                return new LookupResult(loopbacks, TimeSpan.MaxValue);
            }

            var resolver = dns.Port == DNS_PORT
                ? (IRequestResolver)new TcpRequestResolver(dns)
                : new UdpRequestResolver(dns, new TcpRequestResolver(dns), this.resolveTimeout);

            var addressRecords = await GetAddressRecordsAsync(resolver, endPoint.Host, cancellationToken);

            // 过滤掉环回地址、内网/保留地址以及已知的DNS污染地址：
            // 污染地址往往"握手很快"，不过滤就会被稳定选中、稳定失败
            var addresses = (IList<IPAddress>)addressRecords
                .Select(item => item.IPAddress)
                .Where(item => IpAddressFilter.IsInvalid(item) == false)
                .ToArray();

            if (addresses.Count == 0)
            {
                return new LookupResult(addresses, this.minTimeToLive);
            }

            if (addressRecords.Count > addresses.Count)
            {
                this.logger.LogDebug($"{endPoint.Host}@{dns} 过滤掉 {addressRecords.Count - addresses.Count} 个无效/污染IP");
            }

            if (fastSort == true)
            {
                addresses = await OrderByConnectAnyAsync(addresses, endPoint.Port, cancellationToken);
            }

            var timeToLive = addressRecords.Min(item => item.TimeToLive);
            if (timeToLive <= TimeSpan.Zero)
            {
                timeToLive = this.minTimeToLive;
            }
            else if (timeToLive > this.maxTimeToLive)
            {
                timeToLive = this.maxTimeToLive;
            }

            return new LookupResult(addresses, timeToLive);
        }

        /// <summary>
        /// 获取IP记录
        /// </summary>
        /// <param name="resolver"></param>
        /// <param name="domain"></param> 
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IList<IPAddressResourceRecord>> GetAddressRecordsAsync(IRequestResolver resolver, string domain, CancellationToken cancellationToken)
        {
            var addressRecords = new List<IPAddressResourceRecord>();
            if (Socket.OSSupportsIPv4 == true)
            {
                var records = await GetRecordsAsync(RecordType.A);
                addressRecords.AddRange(records);
            }

            if (Socket.OSSupportsIPv6 == true)
            {
                var records = await GetRecordsAsync(RecordType.AAAA);
                addressRecords.AddRange(records);
            }
            return addressRecords;


            async Task<IEnumerable<IPAddressResourceRecord>> GetRecordsAsync(RecordType recordType)
            {
                var request = new Request
                {
                    RecursionDesired = true,
                    OperationCode = OperationCode.Query
                };

                request.Questions.Add(new Question(new Domain(domain), recordType));
                var clientRequest = new ClientRequest(resolver, request);
                var response = await clientRequest.Resolve(cancellationToken);
                return response.AnswerRecords.OfType<IPAddressResourceRecord>();
            }
        }


        /// <summary>
        /// 连接速度排序
        /// </summary>
        /// <param name="addresses"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IList<IPAddress>> OrderByConnectAnyAsync(IList<IPAddress> addresses, int port, CancellationToken cancellationToken)
        {
            if (addresses.Count <= 1)
            {
                return addresses;
            }

            using var controlTokenSource = new CancellationTokenSource(tcpConnectTimeout);
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controlTokenSource.Token);

            var connectTasks = addresses.Select(address => ConnectAsync(address, port, linkedTokenSource.Token));
            var fastestAddress = await await Task.WhenAny(connectTasks);
            controlTokenSource.Cancel();

            if (fastestAddress == null || addresses.First().Equals(fastestAddress))
            {
                return addresses;
            }

            var list = new List<IPAddress> { fastestAddress };
            foreach (var address in addresses)
            {
                if (address.Equals(fastestAddress) == false)
                {
                    list.Add(address);
                }
            }
            return list;
        }

        /// <summary>
        /// 连接指定ip和端口
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private static async Task<IPAddress?> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(address, port, cancellationToken);
                return address;
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
