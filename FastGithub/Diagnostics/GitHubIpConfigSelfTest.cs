using FastGithub.Configuration;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Diagnostics
{
    internal static class GitHubIpConfigSelfTest
    {
        public static int Run()
        {
            var failed = Verify("恢复自动时清理两种固定 IP 并保留其他设置", RestorePreservesSettings());
            failed += Verify("仅有固定 IP 的旧配置恢复后仍可绑定 GitHub 域名段", RestoreKeepsDomain());
            return failed + Verify("社区候选不足三个时只补测未试过的地址并保持社区顺序", BackfillKeepsCommunityRanking());
        }

        private static bool BackfillKeepsCommunityRanking()
        {
            var first = IPAddress.Parse("192.0.2.10");
            var second = IPAddress.Parse("192.0.2.11");
            var unavailable = IPAddress.Parse("192.0.2.12");
            var supplement = IPAddress.Parse("192.0.2.13");
            var ignored = IPAddress.Parse("192.0.2.14");
            var attempted = new List<IPAddress>();
            var selected = GitHubIpPicker.SelectAddressesAsync(
                new[] { first, second }, new[] { first, unavailable, supplement, ignored }, Measure, CancellationToken.None)
                .GetAwaiter().GetResult();

            return selected.SequenceEqual(new[] { second, first, supplement }) &&
                attempted.SequenceEqual(new[] { first, second, unavailable, supplement });

            Task<double?> Measure(IPAddress address, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                attempted.Add(address);
                return Task.FromResult<double?>(address.Equals(unavailable) ? null
                    : address.Equals(first) ? 10d : address.Equals(second) ? 20d : 30d);
            }
        }

        private static bool RestorePreservesSettings()
        {
            var github = JsonNode.Parse(
                """{"ipaddress":"192.0.2.10","IPAddresses":["192.0.2.11"],"TlsSni":true,"Timeout":"00:01:30"}""",
                new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();

            return GitHubIpPicker.ApplyAddressFields(github, Array.Empty<IPAddress>()) &&
                github.Count == 2 &&
                github["TlsSni"]?.GetValue<bool>() == true &&
                github["Timeout"]?.GetValue<string>() == "00:01:30" &&
                GitHubIpPicker.ApplyAddressFields(github, Array.Empty<IPAddress>()) == false;
        }

        private static bool RestoreKeepsDomain()
        {
            var github = new JsonObject { ["IPAddress"] = "192.0.2.10" };
            if (GitHubIpPicker.ApplyAddressFields(github, Array.Empty<IPAddress>()) == false ||
                GitHubIpPicker.ApplyAddressFields(github, Array.Empty<IPAddress>()))
            {
                return false;
            }

            var root = new JsonObject
            {
                ["FastGithub"] = new JsonObject
                {
                    ["DomainConfigs"] = new JsonObject { ["github.com"] = github }
                }
            };
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(root.ToJsonString()));
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
            var options = configuration.GetSection("FastGithub").Get<FastGithubOptions>();
            return options != null && options.DomainConfigs.TryGetValue("github.com", out var domainConfig) &&
                domainConfig.GetFixedAddresses().Length == 0;
        }

        private static int Verify(string name, bool passed)
        {
            Console.WriteLine(passed ? $"  OK   {name}" : $"  FAIL {name}");
            return passed ? 0 : 1;
        }
    }
}
