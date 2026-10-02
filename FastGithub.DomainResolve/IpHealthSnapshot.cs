namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 单个 host + IP 的健康度快照（只读）
    /// </summary>
    public sealed record IpHealthSnapshot
    {
        /// <summary>
        /// 域名
        /// </summary>
        public required string Host { get; init; }

        /// <summary>
        /// IP地址
        /// </summary>
        public required string Address { get; init; }

        /// <summary>
        /// 统计总次数（已按衰减系数折算）
        /// </summary>
        public int Total { get; init; }

        /// <summary>
        /// 失败次数（已按衰减系数折算）
        /// </summary>
        public int Error { get; init; }

        /// <summary>
        /// 连续失败次数
        /// </summary>
        public int KeepErrorCount { get; init; }

        /// <summary>
        /// 成功率
        /// </summary>
        public double SuccessRate { get; init; }

        /// <summary>
        /// 是否处于拉黑状态
        /// </summary>
        public bool Blacklisted { get; init; }

        /// <summary>
        /// 距离解除拉黑还有多少秒
        /// </summary>
        public int BlacklistRemainingSeconds { get; init; }
    }
}
