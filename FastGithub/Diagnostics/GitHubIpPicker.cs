using FastGithub.Configuration;
using FastGithub.DomainResolve;
using FastGithub.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Diagnostics
{
    /// <summary>
    /// 供托盘选优及恢复自动选路使用，备份并更新 GitHub 固定 IP。
    /// </summary>
    internal sealed class GitHubIpPicker
    {
        private const string Host = "github.com";
        private const string ConfigPath = "appsettings/appsettings.github.json";
        private const string DomainConfigKey = "FastGithub:DomainConfigs:" + Host;
        private static readonly string[] sourceUrls =
        {
            "https://raw.hellogithub.com/hosts.json",
            "https://hosts.gitcdn.top/hosts.txt",
            "https://cdn.jsdelivr.net/gh/ittuann/GitHub-IP-hosts@main/hosts",
            "https://gitee.com/fliu2476/github-hosts/raw/main/hosts"
        };

        private static readonly IPAddress[] fallbackAddresses = new[]
        {
            "20.6.7.16", "20.6.7.17", "20.27.163.208", "20.205.243.166", "20.207.73.82",
            "140.82.112.3", "140.82.112.4", "140.82.113.3", "140.82.113.4",
            "140.82.114.3", "140.82.114.4", "140.82.121.3", "140.82.121.4",
            "57.155.223.240", "172.182.252.130"
        }.Select(IPAddress.Parse).ToArray();

        private readonly FastGithubConfig config;
        private readonly IConfiguration settings;
        private readonly string configFile;
        private readonly ILogger<GitHubIpPicker> logger;
        private readonly SemaphoreSlim configurationGate = new(1, 1);

        public GitHubIpPicker(FastGithubConfig config, IConfiguration settings, IHostEnvironment environment, ILogger<GitHubIpPicker> logger)
        {
            this.config = config;
            this.settings = settings;
            this.configFile = Path.Combine(environment.ContentRootPath, ConfigPath);
            this.logger = logger;
        }

        public async Task<(bool Success, string Message)> UpdateAsync(bool restoreAutomatic, CancellationToken cancellationToken)
        {
            if (await this.configurationGate.WaitAsync(0, cancellationToken) == false)
            {
                return (false, "已有一次 GitHub IP 配置操作正在进行。");
            }

            try
            {
                if (File.Exists(this.configFile) == false)
                {
                    return (false, "找不到 appsettings.github.json，请检查 FastGitHub 程序目录。");
                }

                return restoreAutomatic
                    ? this.RestoreAutomatic(cancellationToken)
                    : await this.PickAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                this.logger.LogWarning(ex, "写入 GitHub IP 配置失败");
                return (false, "更新 GitHub IP 配置失败，请查看主程序日志。");
            }
            finally
            {
                this.configurationGate.Release();
            }
        }

        public string DescribeStatus(GitHubConnectionStatus.Snapshot? latest)
        {
            var mode = "当前 GitHub 域名配置未加载。";
            if (this.config.TryGetDomainConfig(Host, out var domainConfig))
            {
                var addresses = domainConfig.GetFixedAddresses();
                mode = addresses.Length == 0
                    ? "当前选路为自动。"
                    : $"当前固定 IP 为 {string.Join("、", addresses.Select(address => address.ToString()))}。";
            }

            if (latest == null)
            {
                return $"{mode}\n本次启动尚无 GitHub HTTPS 成功建连记录。";
            }

            var source = latest.Source switch
            {
                GitHubConnectionSource.Fixed => "固定配置",
                GitHubConnectionSource.DynamicFallback => "动态回退",
                _ => "自动选路"
            };
            return $"{mode}\n最近一次 GitHub HTTPS 成功建连\nIP　{latest.Address}\n来源　{source}\n时间　{latest.ConnectedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }

        private async Task<(bool Success, string Message)> PickAsync(CancellationToken cancellationToken)
        {
            if (this.config.TryGetDomainConfig(Host, out var domainConfig) == false)
            {
                return (false, "找不到 GitHub 配置，请检查 appsettings.github.json。");
            }

            using var sourceClient = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10d),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            sourceClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var sourceCandidates = await Task.WhenAll(
                sourceUrls.Select(url => this.FetchCandidatesAsync(sourceClient, url, cancellationToken)));

            var seen = new HashSet<IPAddress>();
            var candidates = sourceCandidates.SelectMany(addresses => addresses)
                .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                .Where(seen.Add)
                .ToArray();
            var selected = await this.ProbeAsync(candidates, domainConfig, cancellationToken);
            if (selected.Length == 0)
            {
                this.logger.LogInformation("社区来源没有可用的 GitHub IP，尝试内置候选");
                selected = await this.ProbeAsync(fallbackAddresses.Where(seen.Add), domainConfig, cancellationToken);
            }

            if (selected.Length == 0)
            {
                return (false, "没有 IP 通过 Git HTTP 探测，现有配置已保留。");
            }

            var backup = this.SaveAddresses(selected, cancellationToken);
            var result = string.Join("、", selected.Select(address => address.ToString()));
            this.logger.LogInformation("已写入 GitHub 固定 IP {Addresses}，配置备份 {Backup}", result, backup);
            return (true, $"已更新 GitHub 固定 IP：{result}");
        }

        private (bool Success, string Message) RestoreAutomatic(CancellationToken cancellationToken)
        {
            var backup = this.SaveAddresses(Array.Empty<IPAddress>(), cancellationToken);
            if (backup != null)
            {
                this.logger.LogInformation("已清除 appsettings.github.json 中的 GitHub 固定 IP，配置备份 {Backup}", backup);
            }

            if (this.HasAddressesFromOtherProviders())
            {
                return (false, backup == null
                    ? "GitHub 固定 IP 来自其他配置源，请在对应配置中清除。"
                    : "已备份并清除 appsettings.github.json 的固定 IP，其他配置源仍提供 GitHub 固定 IP。");
            }

            return (true, backup == null
                ? "GitHub 已配置为自动选路。"
                : "已备份配置并恢复 GitHub 自动选路，配置重载后用于新连接。");
        }

        private bool HasAddressesFromOtherProviders()
        {
            if (this.settings is not IConfigurationRoot root)
            {
                return false;
            }

            // 清除本文件后，低优先级配置源也可能重新提供固定 IP。
            var providers = root.Providers
                .Where(provider => provider is not JsonConfigurationProvider json ||
                    string.Equals(json.Source.Path?.Replace('\\', '/'), ConfigPath, StringComparison.OrdinalIgnoreCase) == false)
                .Reverse()
                .ToArray();
            var listKey = $"{DomainConfigKey}:{nameof(DomainConfig.IPAddresses)}";
            var entries = providers.SelectMany(provider => provider.GetChildKeys(Array.Empty<string>(), listKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return entries.Length > 0
                ? entries.Any(entry => HasAddress($"{listKey}:{entry}"))
                : HasAddress($"{DomainConfigKey}:{nameof(DomainConfig.IPAddress)}");

            bool HasAddress(string key)
            {
                foreach (var provider in providers)
                {
                    if (provider.TryGet(key, out var value))
                    {
                        return IPAddress.TryParse(value, out _);
                    }
                }

                return false;
            }
        }

        private async Task<IReadOnlyList<IPAddress>> FetchCandidatesAsync(
            System.Net.Http.HttpClient client, string url, CancellationToken cancellationToken)
        {
            try
            {
                var content = await client.GetStringAsync(url, cancellationToken);
                var addresses = HostsContentParser.ParseAddresses(content, Host);
                this.logger.LogInformation("GitHub 选优来源 {Source} 提供 {Count} 个候选 IP", url, addresses.Count);
                return addresses;
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) &&
                cancellationToken.IsCancellationRequested == false)
            {
                this.logger.LogWarning("GitHub 选优来源 {Source} 拉取失败（{Reason}）", url, ex.Message);
                return Array.Empty<IPAddress>();
            }
        }

        private async Task<IPAddress[]> ProbeAsync(
            IEnumerable<IPAddress> candidates, DomainConfig domainConfig, CancellationToken cancellationToken)
        {
            var measured = new List<(IPAddress Address, double BytesPerSecond)>();
            // 串行下载，避免候选相互争抢带宽而扭曲速率排序。
            foreach (var address in candidates)
            {
                try
                {
                    var speed = await GitHubIpProbe.MeasureAsync(address, domainConfig, cancellationToken);
                    if (speed is double bytesPerSecond)
                    {
                        measured.Add((address, bytesPerSecond));
                        this.logger.LogInformation("GitHub IP {Address} 探测通过，下载速率 {Speed:F0} 字节/秒", address, bytesPerSecond);
                    }
                    else
                    {
                        this.logger.LogInformation("GitHub IP {Address} 未返回完整的 Git 智能 HTTP 响应", address);
                    }
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) &&
                    cancellationToken.IsCancellationRequested == false)
                {
                    this.logger.LogInformation("GitHub IP {Address} 探测失败（{Reason}）", address, ex.Message);
                }
            }

            return measured.OrderByDescending(item => item.BytesPerSecond).Take(3).Select(item => item.Address).ToArray();
        }

        private string? SaveAddresses(IPAddress[] selected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = File.ReadAllText(this.configFile);
            var root = JsonNode.Parse(original.TrimStart('\uFEFF'),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (root is not JsonObject configRoot ||
                configRoot["FastGithub"] is not JsonObject options ||
                options["DomainConfigs"] is not JsonObject domains ||
                domains[Host] is not JsonObject github)
            {
                throw new InvalidDataException("appsettings.github.json 缺少 github.com 配置段。");
            }

            if (ApplyAddressFields(github, selected) == false)
            {
                return null;
            }

            var id = Guid.NewGuid().ToString("N");
            var stagedConfig = $"{this.configFile}.{id}.tmp";
            var backup = $"{this.configFile}.bak-{DateTime.Now:yyyyMMdd-HHmmss}-{id}";
            try
            {
                using (var stream = new FileStream(stagedConfig, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    configRoot.WriteTo(writer);
                }

                if (string.Equals(File.ReadAllText(this.configFile), original, StringComparison.Ordinal) == false)
                {
                    throw new IOException("GitHub 配置在写入前已被修改，请重试。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                // 在发布新配置的同时保留原文件，热重载只会读取完整的 JSON。
                File.Replace(stagedConfig, this.configFile, backup);
                return backup;
            }
            finally
            {
                File.Delete(stagedConfig);
            }
        }

        internal static bool ApplyAddressFields(JsonObject github, IReadOnlyList<IPAddress> selected)
        {
            var removedLegacy = github.Remove(nameof(DomainConfig.IPAddress));
            if (selected.Count == 0)
            {
                if (github.Count == 1 && github[nameof(DomainConfig.IPAddresses)] is JsonArray { Count: 0 })
                {
                    return removedLegacy;
                }

                var removedList = github.Remove(nameof(DomainConfig.IPAddresses));
                if (github.Count == 0)
                {
                    // 保留可绑定的 GitHub 域名段，兼容原先只配置了固定 IP 的文件。
                    github[nameof(DomainConfig.IPAddresses)] = new JsonArray();
                    return true;
                }

                return removedLegacy || removedList;
            }

            var addresses = new JsonArray();
            foreach (var address in selected)
            {
                addresses.Add(JsonValue.Create(address.ToString()));
            }
            github[nameof(DomainConfig.IPAddresses)] = addresses;
            return true;
        }
    }
}
