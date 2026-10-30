namespace FastGithub.UI
{
    /// <summary>
    /// 后端 /refresh-ip 的返回结果（JSON）
    /// </summary>
    /// <remarks>
    /// 字段名与 FastGithub.DomainResolve.RefreshIpResult 一致（camelCase 序列化），
    /// 借鉴上游 v2.3.2：让"更新IP"返回真实结果而非一句"已触发"。
    /// 后端为旧版本时返回纯文本，反序列化会失败，UI 按兼容分支处理。
    /// </remarks>
    class RefreshIpResult
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
        /// 成功命中的 hosts 源 URL（v2.6.12 多源 failover）
        /// </summary>
        public string? HostsSource { get; set; }
    }
}
