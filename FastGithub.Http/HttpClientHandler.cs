using FastGithub.Configuration;
using FastGithub.DomainResolve;
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
    class HttpClientHandler : DelegatingHandler
    {
        private readonly DomainConfig domainConfig;
        private readonly IDomainResolver domainResolver;
        // 与 dev-sidecar 的 SpeedTester 对齐：单IP 5秒连不上就换下一个，
        // 10秒会让"3个IP全坏"的最坏情况拖到30秒，用户体感就是卡死。
        // 注意该预算只覆盖 TCP 建连：跨境链路的 TLS 握手（含证书链校验、
        // 可能的 OCSP/CRL 回源）经常超过 5s，把两者压在同一预算内会误杀优质IP。
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);

        // TLS 握手单独给一份更宽的预算，避免把"握手慢但服务正常"的IP判为坏IP
        private readonly TimeSpan tlsHandshakeTimeout = TimeSpan.FromSeconds(10d);

        // 并发赛马中后续候选的错开间隔（RFC 8305 Happy Eyeballs）
        private static readonly TimeSpan RACE_STAGGER = TimeSpan.FromMilliseconds(250d);

        // 并发赛马的候选上限。候选来自 hosts源 + DNS 补充，数量可能不少；
        // 全部并发会同时打出大量握手，而排序靠后的多是劣质IP，不值得为它们建连。
        private const int MAX_RACE_COUNT = 4;

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
        public HttpClientHandler(DomainConfig domainConfig, IDomainResolver domainResolver)
        {
            this.domainConfig = domainConfig;
            this.domainResolver = domainResolver;
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

            // 设置请求头host，修改协议为http
            request.Headers.Host = uri.Host;
            request.RequestUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp }.Uri;

            if (this.domainConfig.Timeout != null)
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.domainConfig.Timeout.Value);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                return await base.SendAsync(request, linkedTokenSource.Token);
            }
            return await base.SendAsync(request, cancellationToken);
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

            // 并发赛马（RFC 8305 Happy Eyeballs 思路）：
            // 原实现是串行逐个尝试，一个坏IP要等满 connectTimeout 才轮到下一个，
            // 3 个候选全坏时用户要干等 15 秒。并发发起后取第一个握手成功的连接，
            // 最坏耗时从 N×超时 降到 1×超时。
            using var raceTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pending = new List<Task<(IPEndPoint EndPoint, Stream? Stream, bool Verified, Exception? Error)>>();

            // 候选可能很多（hosts源 + DNS 补充），全部并发会同时打出大量握手。
            // 取前面若干个即可：排序已按健康度与时延排过，后面的多是劣质IP。
            var raceCandidates = candidates.Take(MAX_RACE_COUNT).ToArray();

            for (var i = 0; i < raceCandidates.Length; i++)
            {
                // 首个立即发起，其余错开 ~250ms（RFC 8305）。
                // 全部同时打出去会在链路拥塞时互相拖慢，错开后通常第一个就能成功，
                // 既拿到并发的容错又不必承担同时建连的开销。
                if (i > 0)
                {
                    try
                    {
                        await Task.Delay(RACE_STAGGER, raceTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                pending.Add(this.RaceConnectAsync(context, ipEndPoint: raceCandidates[i], raceTokenSource.Token));
            }

            try
            {
                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);

                    var (endPoint, stream, verified, error) = await completed;
                    if (error == null && stream != null)
                    {
                        // 胜出的连接已可用，取消其余仍在握手的尝试，回收 socket
                        raceTokenSource.Cancel();

                        // 仅在完成 TLS 握手（证书链校验通过）后才回写健康度。
                        // 纯 http 场景只完成 TCP 连通，证明不了该IP真正可用——
                        // 此时既不上报成功（会把坏IP的惩罚值洗白）也不上报失败
                        // （TCP 确实通了，判失败同样是冤枉），保持统计中性。
                        if (verified)
                        {
                            this.domainResolver.ReportSuccess(context.DnsEndPoint, endPoint.Address);
                        }
                        return stream;
                    }

                    if (error is OperationCanceledException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        this.domainResolver.ReportFailure(context.DnsEndPoint, endPoint.Address);
                        innerExceptions.Add(new HttpConnectTimeoutException(endPoint.Address));
                    }
                    else if (error != null)
                    {
                        this.domainResolver.ReportFailure(context.DnsEndPoint, endPoint.Address);
                        innerExceptions.Add(error);
                    }
                }
            }
            finally
            {
                // 未胜出的连接必须回收，否则并发赛马会泄漏 socket
                foreach (var task in pending)
                {
                    _ = task.ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            t.Result.Stream?.Dispose();
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
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
            if (gate.Wait(0) == false)
            {
                // 该域名已有刷新在进行，避免重复触发
                return;
            }

            try
            {
                await this.domainResolver.RefreshAsync(CancellationToken.None);
            }
            catch (Exception)
            {
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

            // TLS 握手使用独立预算：TCP 已连通的情况下，
            // 握手超时更可能意味着链路慢而非IP坏，不应与TCP失败同等对待
            using var tlsTimeoutSource = new CancellationTokenSource(this.tlsHandshakeTimeout);
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
        /// 并发赛马中的单次连接尝试。
        /// 不抛异常，把结果（成功或异常）打包返回，便于调用方取第一个成功者。
        /// </summary>
        /// <param name="context"></param>
        /// <param name="ipEndPoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<(IPEndPoint EndPoint, Stream? Stream, bool Verified, Exception? Error)> RaceConnectAsync(
            SocketsHttpConnectionContext context,
            IPEndPoint ipEndPoint,
            CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, cancellationToken);
                var (stream, verified) = await this.ConnectAsync(context, ipEndPoint, linkedTokenSource.Token);
                return (ipEndPoint, stream, verified, null);
            }
            catch (Exception ex)
            {
                return (ipEndPoint, null, false, ex);
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
