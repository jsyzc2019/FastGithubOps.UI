using FastGithub.Configuration;
using FastGithub.DomainResolve;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// HttpClientHandler
    /// </summary> 
    public class HttpClientHandler : DelegatingHandler
    {
        private readonly DomainConfig domainConfig;
        private readonly IDomainResolver domainResolver;
        private readonly ILogger<HttpClientHandler>? logger;
        private readonly GitHubConnectionStatus? gitHubConnectionStatus;

        private readonly record struct ConnectionCandidate(IPAddress Address, bool IsFixed);

        // 默认单IP建连预算（TCP+TLS）。跨境链路 RTT 差异极大：github.com 交互请求常在 5s 左右，
        // 而 raw.githubusercontent.com / codeload.github.com 这类承载大文件、git 对象的域名
        // 单条 502 就曾耗时 12s+。默认值仅作兜底，具体域名可用 DomainConfig.ConnectTimeout 覆盖
        // （见 appsettings.github.json）：githubusercontent/codeload 等设为 25s。
        private readonly TimeSpan defaultConnectTimeout = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// 当前域名的建连（TCP+TLS）预算：优先取按域名配置的 ConnectTimeout，否则用默认值。
        /// TLS 握手预算与之联动——若该域名被配置了更长的建连预算，握手自然也该放宽，
        /// 否则"握手慢但服务正常"的IP会被误杀。
        /// </summary>
        private TimeSpan ConnectBudget => this.domainConfig.ConnectTimeout ?? this.defaultConnectTimeout;

        // 通常至多串行尝试 3 个候选。固定池用完这 3 次后，仍允许一次动态回退。
        private const int MAX_TRY_COUNT = 3;

        // 串行尝试候选的总建连预算（不是单IP 的预算）。
        // <para>
        // 单IP 预算 12s × 3 个候选 = 最坏 36s，再叠加 5xx 重试与响应等待，
        // 实测出现过单条请求耗时 75s（POST .../rum responded 202 in 75411ms）——
        // 请求长时间挂起会让浏览器/调用方一起卡住，用户观感就是"卡死"。
        // 这里给整个"找一个可用 IP"的过程封顶，超时即立刻放弃并走刷新逻辑，
        // 让失败快速暴露、尽快进入下一轮重试，而不是在一个注定失败的请求上死等。
        /// </para>
        // <para>
        // 【为何从 20s 下调到 10s】原值 20s 仍偏大：本机实测不可路由地址的 TCP SYN
        // 重传约需 21s，因此单IP 预算（12s）往往先于总预算耗尽，总预算实际只起到
        // "兜底"作用；而失败请求的总等待 = 总预算 + 后续处理，很容易冲到数十秒。
        // 结合实测健康 IP 的 TCP+TLS 耗时仅 0.4~1.1s（跨境 RTT 正常时），
        /// 10s 足以让任一健康候选完成建连，同时把"一个都连不上"的最坏情况
        // 从最坏 36s 压到 10s 以内——失败要快速暴露，才能尽快进入下一轮恢复。
        /// </para>
        private static readonly TimeSpan totalConnectBudget = TimeSpan.FromSeconds(10d);

        // 刷新闸门按域名隔离。
        // 原实现是一个全局 static 令牌，任意域名触发刷新会让其他域名
        // 在最长10秒（hosts源HTTP超时）内无法通过该路径恢复。
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> refreshGates
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// HttpClientHandler
        /// </summary>
        /// <param name="domainConfig"></param>
        /// <param name="domainResolver"></param> 
        public HttpClientHandler(DomainConfig domainConfig, IDomainResolver domainResolver, ILogger<HttpClientHandler>? logger = null)
            : this(domainConfig, domainResolver, logger, null)
        {
        }

        internal HttpClientHandler(DomainConfig domainConfig, IDomainResolver domainResolver,
            ILogger<HttpClientHandler>? logger, GitHubConnectionStatus? gitHubConnectionStatus)
        {
            this.domainConfig = domainConfig;
            this.domainResolver = domainResolver;
            this.logger = logger;
            this.gitHubConnectionStatus = gitHubConnectionStatus;
            this.InnerHandler = this.CreateSocketsHttpHandler();
        }

        /// <summary>
        /// 发送请求
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri == null)
            {
                throw new FastGithubException("必须指定请求的URI");
            }

            // 请求上下文信息
            var isHttps = uri.Scheme == Uri.UriSchemeHttps;
            var tlsSniValue = this.domainConfig.GetTlsSniPattern().WithDomain(uri.Host).WithRandom();
            request.SetRequestContext(new RequestContext(isHttps, tlsSniValue));

            // 设置请求头host，修改协议为http（仅做一次；重试时复用同一已转换的请求）
            request.Headers.Host = uri.Host;
            request.RequestUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri;

            // 域名请求预算覆盖建连和等待上游响应头。收到响应头后，YARP 用自身的活动超时管理正文转发。
            CancellationToken effectiveToken = cancellationToken;
            // 【v2.6.14 P1】遥测 beacon 用 8s fail-fast 预算（其余按域名配置）。
            // 见 TelemetryPolicy：504 里 66% 是 stats/collect beacon，它们吃满 25~30s
            // 只为上报一条浏览器不等结果的统计，故障时还会放大排队。
            var requestTimeout = TelemetryPolicy.LimitTimeout(uri, this.domainConfig.Timeout);
            using var timeoutTokenSource = requestTimeout != null
                ? new CancellationTokenSource(requestTimeout.Value)
                : null;
            using var linkedTokenSource = timeoutTokenSource != null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token)
                : null;
            if (linkedTokenSource != null)
            {
                effectiveToken = linkedTokenSource.Token;
            }

            return await this.SendWithRetryAsync(request, effectiveToken, cancellationToken);
        }

        /// <summary>
        /// 带 5xx 自动重试地发送请求。
        /// <para>
        /// GitHub 边缘对 github.com / raw / codeload 等域名会间歇性返回 502/503/504
        /// （日志中曾出现单条 502 耗时 5s、甚至 12s）。这些响应是上游"成功返回"的 HTTP 报文，
        /// 对幂等请求（GET/HEAD）重试一次往往即可命中正常节点。
        /// 仅在 base.SendAsync 拿到 5xx 时重试：连接层超时（AggregateException）走原有自动刷新逻辑，
        /// 不在此空转；非幂等请求（POST/PUT 等）不重试，避免重复提交。
        /// </para>
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken effectiveToken, CancellationToken userToken)
        {
            // 非幂等请求：直接透传，不重试
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                return await base.SendAsync(request, effectiveToken);
            }

            // 只重试 1 次（最多 2 次尝试）：5xx 多为 GitHub 边缘瞬时过载，一次重试通常即可命中；
            // 重试过多会放大请求量，反而更容易触发对端风控，与"减少阻断"的目标相悖。
            // 且重试复用同一池化连接（仅在连接已死时才新建），不产生额外握手风暴。
            const int maxAttempts = 2;
            var backoffs = new[] { TimeSpan.FromMilliseconds(800d) };

            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // 重试直接调 base（SocketsHttpHandler），跳过外层 HttpClient 的 UserAgent 校验；
                // GET 无请求体，可安全复用同一request 对象。重试不新建并行连接、不做竞速，
                // 由连接池复用既有连接，因此不会放大握手流量。
                response = await base.SendAsync(request, effectiveToken);
                var status = (int)response.StatusCode;

                // 2xx/3xx/4xx 不在重试范围；仅对 5xx（网关/过载类瞬时错误）重试
                if (response.IsSuccessStatusCode || status < 500 || attempt >= maxAttempts)
                {
                    return response;
                }

                this.logger?.LogWarning($"上游返回 {status}（{request.RequestUri}），第 {attempt} 次重试（共 {maxAttempts - 1} 次）");
                response.Dispose();
                if (attempt - 1 < backoffs.Length)
                {
                    await Task.Delay(backoffs[attempt - 1], userToken);
                }
            }
            return response!;
        }

        /// <summary>
        /// 创建转发代理的httpHandler
        /// </summary>
        /// <returns></returns>
        private SocketsHttpHandler CreateSocketsHttpHandler()
        {
            return new SocketsHttpHandler
            {
                Proxy = null,
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                // 反向代理必须透传原始编码：这里若自动解压，
                // 响应体长度与 Content-Length/Content-Encoding 会对不上，下游直接解析失败。
                AutomaticDecompression = DecompressionMethods.None,
                ConnectCallback = this.ConnectCallback,

                // 每个目标站点的并发连接数。跨境链路上一连接常被打满，
                // 默认虽然是不限，但显式给出可避免将来被框架默认值改变。
                MaxConnectionsPerServer = 32,

                // 空闲连接保留时间：GitHub 的请求是密集短连接（API/raw），
                // 连接复用能省掉每次的 TCP+TLS 往返。默认 1 分钟偏保守。
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(3d),

                // 单一连接的最长存活时间。长时间复用同一连接时，
                // 中间设备可能在无通告的情况下掐断，表现为偶发请求失败。
                PooledConnectionLifetime = TimeSpan.FromMinutes(10d),

                // 响应体未读完时需要 drain 才能复用连接，给一个较短的预算，
                // 避免慢客户端长期占用连接池。
                ResponseDrainTimeout = TimeSpan.FromSeconds(5d),

                EnableMultipleHttp2Connections = true
            };
        }

        /// <summary>
        /// 连接回调
        /// </summary>
        /// <param name="context"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            return this.ConnectWithCandidatesAsync(context.DnsEndPoint,
                (endPoint, token) => this.ConnectAsync(context, endPoint, token), cancellationToken);
        }

        internal async ValueTask<Stream> ConnectWithCandidatesAsync(
            DnsEndPoint endPoint,
            Func<IPEndPoint, CancellationToken, ValueTask<(Stream Stream, bool FullyVerified)>> connect,
            CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            var seen = new HashSet<IPAddress>();
            var blacklisted = new List<ConnectionCandidate>();
            var attempted = 0;

            IPAddress.TryParse(endPoint.Host, out var literalAddress);
            var fixedAddresses = literalAddress != null ? new[] { literalAddress } : this.domainConfig.GetFixedAddresses();
            var fixedCandidates = new List<ConnectionCandidate>(MAX_TRY_COUNT);
            foreach (var address in fixedAddresses)
            {
                if (AdmitCandidate(address, isFixed: true) is { } candidate)
                {
                    fixedCandidates.Add(candidate);
                    if (fixedCandidates.Count == MAX_TRY_COUNT)
                    {
                        break;
                    }
                }
            }
            var dynamicCandidates = new List<ConnectionCandidate>();

            if (fixedAddresses.Length == 0)
            {
                // 普通域名沿用原有解析和建连顺序。
                await foreach (var address in this.domainResolver.ResolveAsync(endPoint, cancellationToken))
                {
                    if (AdmitCandidate(address, isFixed: false) is { } candidate)
                    {
                        dynamicCandidates.Add(candidate);
                    }
                }
            }

            using var totalTimeoutSource = new CancellationTokenSource(totalConnectBudget);
            using var totalTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimeoutSource.Token);
            var startedAt = Stopwatch.GetTimestamp();

            // 固定地址先尝试。按候选数为动态回退预留一个时间片，IP 字面量独享拨号时间。
            var fixedDeadline = literalAddress != null
                ? totalConnectBudget
                : totalConnectBudget * fixedCandidates.Count / (fixedCandidates.Count + 1);
            if (await TryConnectAsync(fixedCandidates, fixedDeadline) is { } fixedStream)
            {
                return fixedStream;
            }

            var fixedAttempts = attempted;
            if (fixedAddresses.Length > 0 && literalAddress == null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 共享 DNS 查询可能继续填充缓存。请求最多用剩余时限的三分之一等待解析。
                var resolveBudget = (totalConnectBudget - Stopwatch.GetElapsedTime(startedAt)) / 3;
                if (resolveBudget > TimeSpan.Zero)
                {
                    var resolved = new ConcurrentQueue<IPAddress>();
                    var resolveTask = this.CollectFallbackAddressesAsync(endPoint, resolved, cancellationToken);
                    try
                    {
                        await resolveTask.WaitAsync(resolveBudget, totalTokenSource.Token);
                    }
                    catch (TimeoutException)
                    {
                        this.logger?.LogDebug("{Host} 的动态解析仍在进行，使用预算内已获得的地址", endPoint.Host);
                    }
                    catch (OperationCanceledException) when (totalTimeoutSource.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    foreach (var address in resolved)
                    {
                        if (AdmitCandidate(address, isFixed: false) is { } candidate)
                        {
                            dynamicCandidates.Add(candidate);
                        }
                    }
                }
            }

            // 只有固定和动态池起初都没有可用地址时，才恢复对黑名单内地址的试探。
            if (fixedCandidates.Count == 0 && dynamicCandidates.Count == 0)
            {
                dynamicCandidates.AddRange(blacklisted);
            }

            // 三条固定地址全失败时额外允许一个动态候选。实际拨号仍受同一个总时限约束。
            var fallbackLimit = Math.Max(1, MAX_TRY_COUNT - fixedAttempts);
            if (await TryConnectAsync(dynamicCandidates.Take(fallbackLimit).ToArray(), totalConnectBudget, fixedAttempts > 0) is { } dynamicStream)
            {
                return dynamicStream;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (totalTimeoutSource.IsCancellationRequested || Stopwatch.GetElapsedTime(startedAt) >= totalConnectBudget)
            {
                innerExceptions.Add(new TimeoutException(
                    $"在 {totalConnectBudget.TotalSeconds:F0}s 内未能连接 {endPoint.Host}（已尝试 {attempted} 个候选IP）"));
                this.logger?.LogWarning($"{endPoint.Host} 建连总预算耗尽，已尝试 {attempted} 个候选IP");
            }
            if (innerExceptions.Count == 0)
            {
                innerExceptions.Add(new InvalidOperationException($"{endPoint.Host} 未能解析到任何可用ip"));
            }

            _ = this.RefreshIpAsync(endPoint.Host);
            throw new AggregateException("找不到任何可成功连接的IP，已自动触发IP更新，请稍后重试", innerExceptions);

            ConnectionCandidate? AdmitCandidate(IPAddress address, bool isFixed)
            {
                if (seen.Add(address) == false)
                {
                    return null;
                }

                var candidate = new ConnectionCandidate(address, isFixed);
                if (this.domainResolver.IsBlacklisted(endPoint, address))
                {
                    blacklisted.Add(candidate);
                    innerExceptions.Add(new HttpConnectTimeoutException(address));
                    return null;
                }

                return candidate;
            }

            async ValueTask<Stream?> TryConnectAsync(IReadOnlyList<ConnectionCandidate> candidates, TimeSpan deadline, bool isFallback = false)
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remainingBudget = deadline - Stopwatch.GetElapsedTime(startedAt);
                    if (remainingBudget <= TimeSpan.Zero || totalTimeoutSource.IsCancellationRequested)
                    {
                        break;
                    }

                    // 每次拨号均分该阶段的剩余时间，慢候选给后续候选留出尝试机会。
                    var slice = remainingBudget / (candidates.Count - index);
                    var budget = this.ConnectBudget < slice ? this.ConnectBudget : slice;
                    var candidate = candidates[index];

                    try
                    {
                        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTokenSource.Token);
                        attemptTimeout.CancelAfter(budget);
                        attempted++;
                        var (stream, verified) = await connect(new IPEndPoint(candidate.Address, endPoint.Port), attemptTimeout.Token);

                        // HTTPS 完成 TLS 验证后才回写健康度。纯 HTTP 建连保持中性。
                        if (verified)
                        {
                            this.domainResolver.ReportSuccess(endPoint, candidate.Address);
                            this.gitHubConnectionStatus?.Record(endPoint.Host, candidate.Address, candidate.IsFixed, fixedAddresses.Length > 0);
                        }
                        if (isFallback && candidate.IsFixed == false)
                        {
                            this.logger?.LogInformation("{Host} 固定 IP 建连失败，已使用动态地址 {Address}", endPoint.Host, candidate.Address);
                        }
                        return stream;
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (totalTimeoutSource.IsCancellationRequested)
                        {
                            break;
                        }

                        this.domainResolver.ReportFailure(endPoint, candidate.Address);
                        innerExceptions.Add(new HttpConnectTimeoutException(candidate.Address));
                    }
                    catch (Exception ex)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        this.domainResolver.ReportFailure(endPoint, candidate.Address);
                        innerExceptions.Add(ex);
                    }
                }

                return null;
            }
        }

        private async Task CollectFallbackAddressesAsync(
            DnsEndPoint endPoint, ConcurrentQueue<IPAddress> addresses, CancellationToken cancellationToken)
        {
            // 即使当前请求结束等待，仍观察共享解析任务的异常。
            try
            {
                await foreach (var address in this.domainResolver.ResolveAsync(endPoint, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    addresses.Enqueue(address);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                this.logger?.LogDebug(ex, "{Host} 的动态回退解析失败", endPoint.Host);
            }
        }

        /// <summary>
        /// 找不到任何可成功连接的IP时，自动触发IP更新
        /// 后台执行，不阻塞当前请求；使用静态令牌避免并发重复刷新
        /// </summary>
        /// <returns></returns>
        private async Task RefreshIpAsync(string host)
        {
            var gate = refreshGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));

            // 【恢复速度的关键】等待而不是抢锁。
            // 原实现用 Wait(0)：若已有刷新在进行就直接放弃返回。
            // 但"已有刷新"恰恰意味着刚被阻断、正在恢复——此时放弃等于让这次请求
            // 白等一轮（用户已等到超时），且若前一次刷新刚好在错误的时间点失败，
            // 就再没有下一次机会。改为排队等待（带上限），保证恢复一定会真正执行。
            if (await gate.WaitAsync(TimeSpan.FromSeconds(10d)) == false)
            {
                this.logger?.LogDebug($"{host} IP刷新排队超时，本次不重复触发");
                return;
            }

            try
            {
                await this.domainResolver.RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                this.logger?.LogWarning($"{host} IP刷新失败：{ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 建立连接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>
        /// 数据流，以及一个标志：该连接是否已完成足以证明该IP可用的验证。
        /// <para>
        /// https 场景含 TLS 握手，证书链校验通过才算真正可用；
        /// 纯 http 场景只做到 TCP 连通，此时该标志为 false——
        /// 对端可能立刻 RST 或返回错误响应，把它算作成功会让坏IP长期停留在候选列表首位。
        /// </para>
        /// </returns>
        private async ValueTask<(Stream Stream, bool FullyVerified)> ConnectAsync(SocketsHttpConnectionContext context, IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            var requestContext = context.InitialRequestMessage.GetRequestContext();
            var stream = await ConnectTcpAsync(ipEndPoint, cancellationToken);
            if (requestContext.IsHttps == false)
            {
                return (stream, false);
            }

            var tlsSniValue = requestContext.TlsSniValue.WithIPAddress(ipEndPoint.Address);

            // TLS 握手预算与建连预算联动：若该域名被配置了更长的 ConnectTimeout（如 githubusercontent 25s），
            // 握手自然也该放宽，否则"握手慢但服务正常"的IP会被误杀。
            using var tlsTimeoutSource = new CancellationTokenSource(this.ConnectBudget);
            using var tlsToken = CancellationTokenSource.CreateLinkedTokenSource(tlsTimeoutSource.Token, cancellationToken);
            var sslStream = await AuthenticateTlsAsync(stream, tlsSniValue, ValidateServerCertificate, tlsToken.Token);
            return (sslStream, true);

            // 验证证书有效性
            bool ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
            {
                var ignoreNameMismatch = this.domainConfig.TlsIgnoreNameMismatch;
                var dnsNameMatches = false;
                if (ignoreNameMismatch == false && errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    var domain = context.DnsEndPoint.Host;
                    var dnsNames = ReadDnsNames(cert);
                    dnsNameMatches = dnsNames.Any(dns => IsMatch(dns, domain));
                }

                return IsCertificateAcceptable(errors, ignoreNameMismatch, dnsNameMatches);
            }
        }

        internal static async ValueTask<NetworkStream> ConnectTcpAsync(IPEndPoint ipEndPoint, CancellationToken cancellationToken)
        {
            var socket = TcpSocketFactory.Create(ipEndPoint.AddressFamily);
            try
            {
                await socket.ConnectAsync(ipEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal static async ValueTask<SslStream> AuthenticateTlsAsync(
            NetworkStream stream,
            TlsSniPattern tlsSniValue,
            RemoteCertificateValidationCallback validateServerCertificate,
            CancellationToken cancellationToken)
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tlsSniValue.Value,
                    RemoteCertificateValidationCallback = validateServerCertificate
                }, cancellationToken);
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 证书策略判定（纯函数，供 CandidatePolicySelfTest 自检）。
        /// <para>
        /// 【v2.6.13 A3，借鉴上游 v2.5.0】证书链错误（不受信任/过期/吊销）**绝不放行**。
        /// 旧实现在 <c>TlsIgnoreNameMismatch</c> 分支里直接 <c>return true</c>，
        /// 于是「名称不匹配 + 链错误」组合——恰是污染 IP 背后中间人的典型特征——
        /// 被当成可信证书放行，这类 IP 会凭"响应快"排到候选头部，
        /// 真实请求阶段却收不到数据，表现为页面转圈/时好时坏。
        /// TlsIgnoreNameMismatch 的语义只允许放过"证书名与域名不符"（不发 SNI 时的正常现象）。
        /// </para>
        /// </summary>
        /// <param name="errors">SSL 策略错误</param>
        /// <param name="ignoreNameMismatch">域名配置是否忽略名称不匹配</param>
        /// <param name="dnsNameMatches">证书 SAN/CN 是否仍与域名匹配</param>
        internal static bool IsCertificateAcceptable(
            System.Net.Security.SslPolicyErrors errors,
            bool ignoreNameMismatch,
            bool dnsNameMatches)
        {
            if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors))
            {
                return false;
            }

            if (errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                return ignoreNameMismatch || dnsNameMatches;
            }

            return errors == System.Net.Security.SslPolicyErrors.None;
        }

        /// <summary>
        /// 读取使用的DNS名称
        /// </summary>
        /// <param name="cert"></param>
        /// <returns></returns>
        private static IEnumerable<string> ReadDnsNames(X509Certificate? cert)
        {
            if (cert is X509Certificate2 x509)
            {
                var extension = x509.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                if (extension != null)
                {
                    return extension.EnumerateDnsNames();
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 比较域名
        /// </summary>
        /// <param name="dnsName"></param>
        /// <param name="domain"></param>
        /// <returns></returns>
        private static bool IsMatch(string dnsName, string? domain)
        {
            if (domain == null)
            {
                return false;
            }
            if (dnsName == domain)
            {
                return true;
            }
            if (dnsName[0] == '*')
            {
                return domain.EndsWith(dnsName[1..]);
            }
            return false;
        }
    }
}
