using FastGithub.Configuration;
using FastGithub.DomainResolve;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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

        // 串行尝试的候选上限。正常情况下，按「健康度 + 时延」排序后的首个 IP 就能连上，
        // 该上限只是约束「连续多个 IP 全坏」时的最坏耗时，不是并发数量。
        private const int MAX_TRY_COUNT = 3;

        // 串行尝试候选的总建连预算（不是单IP 的预算）。
        // <para>
        // 单IP 预算 12s × 3 个候选 = 最坏 36s，再叠加 5xx 重试与响应等待，
        // 实测出现过单条请求耗时 75s（POST .../rum responded 202 in 75411ms）——
        // 请求长时间挂起会让浏览器/调用方一起卡住，用户观感就是"卡死"。
        // 这里给整个"找一个可用 IP"的过程封顶，超时即立刻放弃并走刷新逻辑，
        // 让失败快速暴露、尽快进入下一轮重试，而不是在一个注定失败的请求上死等。
        /// </para>
        private static readonly TimeSpan totalConnectBudget = TimeSpan.FromSeconds(20d);

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
        {
            this.domainConfig = domainConfig;
            this.domainResolver = domainResolver;
            this.logger = logger;
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

            // 整体请求预算（建连+传输+下载），按域名可覆盖；未设置则不限制单请求总时长。
            CancellationToken effectiveToken = cancellationToken;
            using var timeoutTokenSource = this.domainConfig.Timeout != null
                ? new CancellationTokenSource(this.domainConfig.Timeout.Value)
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
        private async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            var candidates = new List<IPEndPoint>();
            var blacklisted = new List<IPEndPoint>();

            await foreach (var ipEndPoint in this.GetIPEndPointsAsync(context.DnsEndPoint, cancellationToken))
            {
                // 已被判定为不可用的IP不再浪费一次连接超时等待
                if (this.domainResolver.IsBlacklisted(context.DnsEndPoint, ipEndPoint.Address))
                {
                    blacklisted.Add(ipEndPoint);
                    innerExceptions.Add(new HttpConnectTimeoutException(ipEndPoint.Address));
                    continue;
                }
                candidates.Add(ipEndPoint);
            }

            // 兜底：若候选全部被拉黑，则强制回退，把已拉黑的IP也试一轮。
            // 否则一次网络瞬断（WiFi抖动/网关重启）就能让某个域名的全部IP同时进黑名单，
            // 该域名将在 BlacklistDuration 内完全不可用——"全挂"这个触发条件
            // 天然就是"全黑"这个故障态。
            if (candidates.Count == 0 && blacklisted.Count > 0)
            {
                candidates.AddRange(blacklisted);
            }

            // 串行尝试：按「健康度 + 时延」排序后逐个连接，首个成功即返回，
            // 随后由 SocketsHttpHandler 连接池长期复用该连接（粘性）。
            //
            // 【为何不能并发竞速多个 IP】
            // 早期版本在这里并发向最多 4 个候选 IP 同时发起 TCP+TLS 握手，谁先成功用谁、
            // 其余连接在 TLS 握手完成后立刻 Dispose（等同 RST）。这带来两个严重后果：
            //   1) 单次请求就在多个 GitHub 边缘 IP 上同时惊扰出完整 TLS 会话又迅速丢弃，
            //      在服务端/风控看来与端口扫描、攻击流量高度相似，是「反复被阻断」的直接原因；
            //   2) 赢家随机、IP 频繁漂移，叠加 DoH 周期重解析与每秒测速重排，
            //      同一域名不断换 IP，进一步触发对端风控。
            // 基线 v2.3.x 之所以「基本不会被阻断」，正是它每次只建一条连接并保持粘性。
            // 这里恢复该模型：慢/不通的根因交给「更准的 IP 排序 + 健康度反馈 + DoH 新鲜解析」解决，
            // 而不是靠并发抢跑——抢占只会更快地撞上风控。
            // 整个"找一个可用 IP"的过程封顶，避免串行逐个等超时叠加成分钟级挂起。
            using var totalTimeoutSource = new CancellationTokenSource(totalConnectBudget);
            using var totalTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, totalTimeoutSource.Token);

            foreach (var ipEndPoint in candidates.Take(MAX_TRY_COUNT))
            {
                try
                {
                    using var timeoutTokenSource = new CancellationTokenSource(this.ConnectBudget);
                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, totalTokenSource.Token);
                    var (stream, verified) = await this.ConnectAsync(context, ipEndPoint, linkedTokenSource.Token);

                    // 仅在完成 TLS 握手（证书链校验通过）后才回写健康度。
                    // 纯 http 场景只完成 TCP 连通，证明不了该IP真正可用，保持统计中性。
                    if (verified)
                    {
                        this.domainResolver.ReportSuccess(context.DnsEndPoint, ipEndPoint.Address);
                    }
                    return stream;
                }
                catch (OperationCanceledException)
                {
                    // 上层（浏览器/调用方）主动取消：如实抛出，不记为IP 失败
                    cancellationToken.ThrowIfCancellationRequested();

                    // 总预算耗尽：说明这批候选整体不可用（或网络极慢），立即停止逐个试，
                    // 交给下面的"找不到可用IP → 触发刷新 + 重试"路径尽快恢复。
                    if (totalTimeoutSource.IsCancellationRequested)
                    {
                        innerExceptions.Add(new TimeoutException(
                            $"在 {totalConnectBudget.TotalSeconds:F0}s 内未能连接 {context.DnsEndPoint.Host}（已尝试 {innerExceptions.Count} 个候选IP）"));
                        this.logger?.LogWarning($"{context.DnsEndPoint.Host} 建连总预算耗尽，放弃剩余候选并触发IP更新");
                        break;
                    }

                    this.domainResolver.ReportFailure(context.DnsEndPoint, ipEndPoint.Address);
                    innerExceptions.Add(new HttpConnectTimeoutException(ipEndPoint.Address));
                }
                catch (Exception ex)
                {
                    this.domainResolver.ReportFailure(context.DnsEndPoint, ipEndPoint.Address);
                    innerExceptions.Add(ex);
                }
            }

            if (innerExceptions.Count == 0)
            {
                // 走到这里说明连一个候选IP都没拿到（域名解析为空），不是连接超时。
                // 语义上区别于 HttpConnectTimeoutException，否则上层无法区分两种故障。
                innerExceptions.Add(new InvalidOperationException($"{context.DnsEndPoint.Host} 未能解析到任何可用ip"));
            }

            // 找不到任何可成功连接的IP时，自动触发IP更新，便于下次请求恢复
            _ = this.RefreshIpAsync(context.DnsEndPoint.Host);
            throw new AggregateException("找不到任何可成功连接的IP，已自动触发IP更新，请稍后重试", innerExceptions);
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
            var socket = TcpSocketFactory.Create(ipEndPoint.AddressFamily);
            await socket.ConnectAsync(ipEndPoint, cancellationToken);
            var stream = new NetworkStream(socket, ownsSocket: true);

            var requestContext = context.InitialRequestMessage.GetRequestContext();
            if (requestContext.IsHttps == false)
            {
                return (stream, false);
            }

            var tlsSniValue = requestContext.TlsSniValue.WithIPAddress(ipEndPoint.Address);
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);

            // TLS 握手预算与建连预算联动：若该域名被配置了更长的 ConnectTimeout（如 githubusercontent 25s），
            // 握手自然也该放宽，否则"握手慢但服务正常"的IP会被误杀。
            using var tlsTimeoutSource = new CancellationTokenSource(this.ConnectBudget);
            using var tlsToken = CancellationTokenSource.CreateLinkedTokenSource(tlsTimeoutSource.Token, cancellationToken);
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = tlsSniValue.Value,
                RemoteCertificateValidationCallback = ValidateServerCertificate
            }, tlsToken.Token);

            return (sslStream, true);

            // 验证证书有效性
            bool ValidateServerCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
            {
                if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                {
                    if (this.domainConfig.TlsIgnoreNameMismatch == true)
                    {
                        return true;
                    }

                    var domain = context.DnsEndPoint.Host;
                    var dnsNames = ReadDnsNames(cert);
                    return dnsNames.Any(dns => IsMatch(dns, domain));
                }

                return errors == SslPolicyErrors.None;
            }
        }

        /// <summary>
        /// 解析为IPEndPoint
        /// </summary>
        /// <param name="dnsEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async IAsyncEnumerable<IPEndPoint> GetIPEndPointsAsync(DnsEndPoint dnsEndPoint, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (IPAddress.TryParse(dnsEndPoint.Host, out var address))
            {
                yield return new IPEndPoint(address, dnsEndPoint.Port);
            }
            else
            {
                if (this.domainConfig.IPAddress != null)
                {
                    yield return new IPEndPoint(this.domainConfig.IPAddress, dnsEndPoint.Port);
                }

                await foreach (var item in this.domainResolver.ResolveAsync(dnsEndPoint, cancellationToken))
                {
                    yield return new IPEndPoint(item, dnsEndPoint.Port);
                }
            }
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
