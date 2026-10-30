using System;
using System.Collections.Generic;
using System.Net;

namespace FastGithub.Configuration
{
    /// <summary>
    /// FastGithub的配置
    /// </summary>
    public class FastGithubOptions
    {
        /// <summary>
        /// 默认的http代理端口
        /// </summary>
        public const int DefaultHttpProxyPort = 38457;

        /// <summary>
        /// 在线hosts源列表（按序 failover：前一个不可用自动尝试下一个）。
        /// <para>
        /// 【v2.6.12 B2】生态里的活跃源（见 docs/生态调研-可借鉴点-20261031.md）：
        /// hellogithub（JSON，原单源）、GitHub520（文本）、ineo6（文本，GitLab 托管）。
        /// 三源同时失效的概率远低于单源，hosts 通道的可用性从"赌一个源"变成"三选一"。
        /// </para>
        /// </summary>
        public static readonly string[] DefaultHostsUrls = new[]
        {
            "https://raw.hellogithub.com/hosts.json",
            "https://raw.githubusercontent.com/521xueweihan/GitHub520/main/hosts",
            "https://gitlab.com/ineo6/hosts/-/raw/master/next-hosts",
        };

        /// <summary>
        /// 默认的在线hosts源地址（v2.6.11 及以前的单源配置，保留兼容；
        /// 未配置 <see cref="HostsUrls"/> 时仍以此为准）
        /// </summary>
        public const string DefaultHostsUrl = "https://raw.hellogithub.com/hosts";

        /// <summary>
        /// http代理端口
        /// </summary>
        public int HttpProxyPort { get; set; } = DefaultHttpProxyPort;

        /// <summary>
        /// 在线hosts源列表，按配置顺序 failover。
        /// <para>
        /// 留空数组表示回退到 <see cref="HostsUrl"/>；两者都为空表示完全不使用在线hosts源。
        /// </para>
        /// <para>
        /// 安全提示：这是一个「完全信任」的配置项——本程序会把源返回的内容
        /// 直接当作域名-&gt;IP映射使用（并经无效IP过滤）。请确认每个源由可信方长期维护。
        /// </para>
        /// </summary>
        public string[] HostsUrls { get; set; } = DefaultHostsUrls;

        /// <summary>
        /// 在线hosts源地址（单源，v2.6.11 及以前的形式，保留兼容）。
        /// <para>
        /// 留空（""）且 <see cref="HostsUrls"/> 也为空表示完全不使用在线hosts源。
        /// </para>
        /// </summary>
        public string HostsUrl { get; set; } = DefaultHostsUrl;

        /// <summary>
        /// 是否启用 GitHub 官方 meta 段候选池扩充（默认开启）。
        /// <para>
        /// 【v2.6.12 B1】api.github.com/meta 公开 web/api/git 官方 CIDR 段。
        /// 解析 github.com 系域名时若候选不足，从官方段采样补充候选，
        /// 经 TCP 握手竞速与健康度筛选后进入候选池——直击"权威 DNS 只回 1 条 A
        /// 导致候选池=1"的痛点。关闭后完全退回纯 DNS 解析。
        /// </para>
        /// </summary>
        public bool MetaCandidateExpand { get; set; } = true;

        /// <summary>
        /// 回退的dns
        /// </summary>
        public IPEndPoint[] FallbackDns { get; set; } = Array.Empty<IPEndPoint>();

        /// <summary>
        /// 代理的域名配置
        /// </summary>
        public Dictionary<string, DomainConfig> DomainConfigs { get; set; } = new();
    }
}
