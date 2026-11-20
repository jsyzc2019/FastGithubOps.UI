using FastGithub.Configuration;
using FastGithub.DomainResolve;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// 用模拟拨号和解析验证固定 IP 优先及动态回退。
    /// </summary>
    public static class HttpCandidateSelfTest
    {
        public static int Run()
        {
            var fixedAddresses = new[]
            {
                IPAddress.Parse("192.0.2.10"),
                IPAddress.Parse("192.0.2.11"),
                IPAddress.Parse("192.0.2.12")
            };
            var dynamicAddress = IPAddress.Parse("192.0.2.20");

            var failed = VerifyOrder("固定 IP 成功时直接返回",
                new DomainConfig { IPAddress = fixedAddresses[0] }, fixedAddresses[0], new[] { dynamicAddress },
                $"拨号 {fixedAddresses[0]}");
            failed += VerifyOrder("三个固定 IP 均失败时尝试去重后的动态地址",
                new DomainConfig { IPAddresses = fixedAddresses }, dynamicAddress, new[] { fixedAddresses[0], dynamicAddress },
                $"拨号 {fixedAddresses[0]}", $"拨号 {fixedAddresses[1]}", $"拨号 {fixedAddresses[2]}", "动态解析", $"拨号 {dynamicAddress}");
            return failed;
        }

        private static int VerifyOrder(string name, DomainConfig config, IPAddress reachable,
            IPAddress[] resolved, params string[] expected)
        {
            var steps = new List<string>();
            using var handler = new HttpClientHandler(config, new ResolverStub(steps, resolved));
            try
            {
                using var stream = handler.ConnectWithCandidatesAsync(new DnsEndPoint("github.com", 443), Connect, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAIL {name}（{ex.Message}）");
                return 1;
            }

            var passed = steps.SequenceEqual(expected);
            Console.WriteLine(passed ? $"  OK   {name}" : $"  FAIL {name}（实际顺序 {string.Join("、", steps)}）");
            return passed ? 0 : 1;

            ValueTask<(Stream Stream, bool FullyVerified)> Connect(IPEndPoint endPoint, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                steps.Add($"拨号 {endPoint.Address}");
                return endPoint.Address.Equals(reachable)
                    ? ValueTask.FromResult<(Stream, bool)>((Stream.Null, true))
                    : ValueTask.FromException<(Stream, bool)>(new IOException("模拟连接失败"));
            }
        }

        private sealed class ResolverStub(List<string> steps, IPAddress[] resolved) : IDomainResolver
        {
            public async IAsyncEnumerable<IPAddress> ResolveAsync(
                DnsEndPoint endPoint, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                steps.Add("动态解析");
                await Task.Yield();
                foreach (var address in resolved)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return address;
                }
            }

            public bool IsBlacklisted(DnsEndPoint endPoint, IPAddress address) => false;

            public void ReportSuccess(DnsEndPoint endPoint, IPAddress address) { }

            public void ReportFailure(DnsEndPoint endPoint, IPAddress address) { }

            public Task TestSpeedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<RefreshIpResult> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(new RefreshIpResult());

            public Task<RefreshIpResult> RefreshHostsAsync(CancellationToken cancellationToken = default) => this.RefreshAsync(cancellationToken);

            public IReadOnlyList<IpHealthSnapshot> GetIpHealth(bool includeHealthy = true, int maxCount = 200) => Array.Empty<IpHealthSnapshot>();
        }
    }
}
