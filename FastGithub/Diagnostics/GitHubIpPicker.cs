using FastGithub.Configuration;
using FastGithub.DomainResolve;
using FastGithub.Http;
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
    /// 供托盘手动选优使用。合并候选、实测 Git 下载并备份更新固定 IP。
    /// </summary>
    internal sealed class GitHubIpPicker
    {
        private const string Host = "github.com";
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
        private readonly string configFile;
        private readonly ILogger<GitHubIpPicker> logger;
        private readonly SemaphoreSlim selectionGate = new(1, 1);

        public GitHubIpPicker(FastGithubConfig config, IHostEnvironment environment, ILogger<GitHubIpPicker> logger)
        {
            this.config = config;
            this.configFile = Path.Combine(environment.ContentRootPath, "appsettings", "appsettings.github.json");
            this.logger = logger;
        }

        public async Task<(bool Success, string Message)> PickAsync(CancellationToken cancellationToken)
        {
            if (await this.selectionGate.WaitAsync(0, cancellationToken) == false)
            {
                return (false, "已有一次 GitHub IP 选优正在运行。");
            }

            try
            {
                if (File.Exists(this.configFile) == false || this.config.TryGetDomainConfig(Host, out var domainConfig) == false)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                this.logger.LogWarning(ex, "写入 GitHub IP 配置失败");
                return (false, "更新 GitHub IP 配置失败，请查看主程序日志。");
            }
            finally
            {
                this.selectionGate.Release();
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

        private string SaveAddresses(IPAddress[] selected, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = JsonNode.Parse(File.ReadAllText(this.configFile).TrimStart('\uFEFF'),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (root is not JsonObject configRoot ||
                configRoot["FastGithub"] is not JsonObject options ||
                options["DomainConfigs"] is not JsonObject domains ||
                domains[Host] is not JsonObject github)
            {
                throw new InvalidDataException("appsettings.github.json 缺少 github.com 配置段。");
            }

            var addresses = new JsonArray();
            foreach (var address in selected)
            {
                addresses.Add(JsonValue.Create(address.ToString()));
            }
            github["IPAddresses"] = addresses;
            github.Remove("IPAddress");

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
    }
}
