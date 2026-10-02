using FastGithub.DomainResolve;
using FastGithub.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Diagnostics
{
    /// <summary>
    /// 连通性探测
    /// <para>
    /// 回答两个问题：目标域名现在到底通不通，以及走哪个 IP 最快。
    /// 健康度统计只能反映历史成功/失败，无法回答"当前链路质量如何"，
    /// 而"加速到底有没有生效"恰恰需要后者——否则只能靠用户主观感受判断。
    /// </para>
    /// </summary>
    public sealed class ConnectivityProbe
    {
        private readonly IDomainResolver domainResolver;
        private readonly TimeSpan probeTimeout = TimeSpan.FromSeconds(6d);

        /// <summary>
        /// 默认探测的 GitHub 相关域名
        /// </summary>
        public static IReadOnlyList<string> DefaultHosts { get; } = new[]
        {
            "github.com",
            "api.github.com",
            "codeload.github.com",
            "raw.githubusercontent.com",
            "objects.githubusercontent.com",
            "github.global.ssl.fastly.net"
        };

        public ConnectivityProbe(IDomainResolver domainResolver)
        {
            this.domainResolver = domainResolver;
        }

        /// <summary>
        /// 探测一组域名的连通性
        /// </summary>
        /// <param name="hosts">域名列表</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IReadOnlyList<HostProbeResult>> ProbeAsync(IEnumerable<string> hosts, CancellationToken cancellationToken)
        {
            var tasks = hosts.Select(host => this.ProbeHostAsync(host, 443, cancellationToken));
            return await Task.WhenAll(tasks);
        }

        /// <summary>
        /// 探测单个域名
        /// </summary>
        /// <param name="host"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<HostProbeResult> ProbeHostAsync(string host, int port, CancellationToken cancellationToken)
        {
            var endPoint = new DnsEndPoint(host, port);

            var addresses = new List<IPAddress>();
            await foreach (var address in this.domainResolver.ResolveAsync(endPoint, cancellationToken))
            {
                addresses.Add(address);
            }

            if (addresses.Count == 0)
            {
                return new HostProbeResult
                {
                    Host = host,
                    Reachable = false,
                    Error = "未解析到任何IP"
                };
            }

            // 并发探测所有候选IP，取第一个完成握手的（同时也是最快的那个）
            using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var tasks = addresses
                .Select(address => this.ProbeAddressAsync(address, port, host, tokenSource.Token))
                .ToArray();

            var stopWatch = Stopwatch.StartNew();
            var failures = 0;
            IpProbeResult? winner = null;

            var pending = tasks.ToList();
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);

                var result = await completed;
                if (result.Success)
                {
                    winner = result;
                    tokenSource.Cancel();
                    break;
                }
                failures++;
            }
            stopWatch.Stop();

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

            return new HostProbeResult
            {
                Host = host,
                Reachable = winner != null,
                TotalIpCount = addresses.Count,
                FailedIpCount = failures,
                Address = winner?.Address?.ToString(),
                HandshakeMs = winner?.ElapsedMs,
                ElapsedMs = (long)stopWatch.Elapsed.TotalMilliseconds,
                Error = winner == null ? $"{addresses.Count} 个候选IP均不可用" : null
            };
        }

        /// <summary>
        /// 探测单个IP：完成 TCP 连接与 TLS 握手
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="host">SNI</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IpProbeResult> ProbeAddressAsync(IPAddress address, int port, string host, CancellationToken cancellationToken)
        {
            var stopWatch = Stopwatch.StartNew();
            Socket? socket = null;
            SslStream? sslStream = null;
            try
            {
                using var timeoutSource = new CancellationTokenSource(this.probeTimeout);
                using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

                socket = TcpSocketFactory.Create(address.AddressFamily);
                await socket.ConnectAsync(new IPEndPoint(address, port), linkedSource.Token);

                sslStream = new SslStream(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    // 探测只关心握手能否完成，证书校验由真实请求负责
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                }, linkedSource.Token);

                var elapsed = (long)stopWatch.Elapsed.TotalMilliseconds;
                return new IpProbeResult(true, address, elapsed, sslStream);
            }
            catch (Exception)
            {
                socket?.Dispose();
                sslStream?.Dispose();
                return new IpProbeResult(false, address, (long)stopWatch.Elapsed.TotalMilliseconds, null);
            }
        }

        /// <summary>
        /// 单个IP的探测结果
        /// </summary>
        private sealed record IpProbeResult(bool Success, IPAddress? Address, long ElapsedMs, Stream? Stream);

        /// <summary>
        /// 单个域名的探测结果
        /// </summary>
        public sealed record HostProbeResult
        {
            public required string Host { get; init; }
            public bool Reachable { get; init; }
            public int TotalIpCount { get; init; }
            public int FailedIpCount { get; init; }
            public string? Address { get; init; }
            public long? HandshakeMs { get; init; }
            public long ElapsedMs { get; init; }
            public string? Error { get; init; }
        }
    }
}
