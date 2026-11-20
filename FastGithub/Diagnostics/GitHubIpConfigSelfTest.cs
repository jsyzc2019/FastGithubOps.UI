using FastGithub.Configuration;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FastGithub.Diagnostics
{
    internal static class GitHubIpConfigSelfTest
    {
        public static int Run()
        {
            var failed = Verify("恢复自动时清理两种固定 IP 并保留其他设置", RestorePreservesSettings());
            return failed + Verify("仅有固定 IP 的旧配置恢复后仍可绑定 GitHub 域名段", RestoreKeepsDomain());
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
