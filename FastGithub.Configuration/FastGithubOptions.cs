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
        /// 默认的在线hosts源地址
        /// </summary>
        public const string DefaultHostsUrl = "https://raw.hellogithub.com/hosts";

        /// <summary>
        /// http代理端口
        /// </summary>
        public int HttpProxyPort { get; set; } = DefaultHttpProxyPort;

        /// <summary>
        /// 在线hosts源地址。
        /// <para>
        /// 留空（""）表示完全不使用在线hosts源，只依赖本机DNS解析。
        /// </para>
        /// <para>
        /// 安全提示：这是一个「完全信任」的配置项——本程序会把该地址返回的内容
        /// 直接当作域名-&gt;IP映射使用。若该域名过期后被他人重新注册，
        /// 新持有者即可向所有用户下发任意hosts记录。请确认该源由你信任且长期维护，
        /// 或在配置中替换为你自己的地址。
        /// </para>
        /// </summary>
        public string HostsUrl { get; set; } = DefaultHostsUrl;

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
