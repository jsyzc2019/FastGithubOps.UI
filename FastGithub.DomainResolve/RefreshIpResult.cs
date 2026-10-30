namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP刷新结果
    /// </summary>
    /// <remarks>
    /// 【v2.6.11 借鉴上游 v2.3.2】此前 /refresh-ip 只回一句"IP更新已触发"，
    /// 用户点了刷新却永远不知道结果（刷了多少域名、剩多少可用IP、hosts源成败）。
    /// 现在把真实结果带回 UI，让"更新IP"从祈祷变成可验证的动作。
    /// </remarks>
    public sealed class RefreshIpResult
    {
        /// <summary>
        /// 参与测速的域名数量
        /// </summary>
        public int DomainCount { get; set; }

        /// <summary>
        /// 测速后可用的IP数量
        /// </summary>
        public int AddressCount { get; set; }

        /// <summary>
        /// 在线hosts源是否更新成功
        /// </summary>
        public bool HostsUpdated { get; set; }

        /// <summary>
        /// 在线hosts源包含的域名数量
        /// </summary>
        public int HostsDomainCount { get; set; }

        /// <summary>
        /// 在线hosts源更新失败的原因
        /// </summary>
        public string? HostsError { get; set; }

        /// <summary>
        /// 成功命中的 hosts 源 URL（v2.6.12 多源 failover 后定位实际来源）
        /// </summary>
        public string? HostsSource { get; set; }
    }
}
