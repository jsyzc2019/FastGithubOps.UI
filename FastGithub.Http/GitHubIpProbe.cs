using FastGithub.Configuration;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// 通过指定 IP 测量 GitHub 智能 HTTP 引用发现请求。
    /// </summary>
    public static class GitHubIpProbe
    {
        // 与反向代理一致，HTTP 请求直接写入已完成 TLS 握手的连接。
        private static readonly Uri probeUri = new("http://github.com/git/git.git/info/refs?service=git-upload-pack");

        /// <summary>
        /// 返回完整下载的平均速率（字节/秒）。响应不符合 Git 智能 HTTP 时返回空值。
        /// </summary>
        public static async Task<double?> MeasureAsync(IPAddress address, DomainConfig config, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10d));

            var tlsSni = config.GetTlsSniPattern().WithDomain(probeUri.Host).WithRandom().WithIPAddress(address);
            using var client = new System.Net.Http.HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(4d),
                ConnectCallback = async (_, token) =>
                {
                    var stream = await HttpClientHandler.ConnectTcpAsync(new IPEndPoint(address, 443), token);
                    return await HttpClientHandler.AuthenticateTlsAsync(stream, tlsSni, ValidateCertificate, token);
                }
            });

            var startTime = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK ||
                string.Equals(response.Content.Headers.ContentType?.MediaType, "application/x-git-upload-pack-advertisement", StringComparison.OrdinalIgnoreCase) == false)
            {
                return null;
            }

            using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[16 * 1024];
            long size = 0;
            int count;
            while ((count = await content.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
            {
                size += count;
            }

            return size > 0 ? size / Math.Max(Stopwatch.GetElapsedTime(startTime).TotalSeconds, 0.001d) : null;
        }

        private static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            // SNI 可以为空或自定义，写入固定配置前仍须验证证书覆盖 github.com。
            return certificate is X509Certificate2 x509 &&
                x509.MatchesHostname(probeUri.Host, allowCommonName: false) &&
                HttpClientHandler.IsCertificateAcceptable(errors, ignoreNameMismatch: false, dnsNameMatches: true);
        }
    }
}
