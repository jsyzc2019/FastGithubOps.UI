using System;
using System.Net;

namespace FastGithub.Configuration
{
    /// <summary>
    /// 域名配置
    /// </summary>
    public record DomainConfig
    {
        /// <summary>
        /// 是否发送SNI
        /// </summary>
        public bool TlsSni { get; init; }

        /// <summary>
        /// 自定义SNI值的表达式
        /// </summary>
        public string? TlsSniPattern { get; init; }

        /// <summary>
        /// 是否忽略服务器证书域名不匹配
        /// 当不发送SNI时服务器可能发回域名不匹配的证书
        /// </summary>
        public bool TlsIgnoreNameMismatch { get; init; }

        /// <summary>
        /// 使用的ip地址
        /// </summary>
        public IPAddress? IPAddress { get; init; }

        /// <summary>
        /// 请求超时时长（整体：建连+传输+下载的总预算）
        /// </summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>
        /// 单个候选IP的建连（TCP+TLS）预算。
        /// <para>
        /// 跨境链路 RTT 差异极大：github.com 交互请求常在 5s 左右，而
        /// raw.githubusercontent.com / codeload.github.com 这类承载大文件、git 对象的域名
        /// 单条 502 就曾耗时 12s+。若所有域名共用一份建连预算，慢域名的新鲜连接会被误杀。
        /// 这里允许按域名覆盖；未设置时回退到 HttpClientHandler / IPAddressService 的默认值。
        /// </para>
        /// </summary>
        public TimeSpan? ConnectTimeout { get; init; }

        /// <summary>
        /// 目的地
        /// 格式为相对或绝对uri
        /// </summary>
        public Uri? Destination { get; init; }

        /// <summary>
        /// 自定义响应
        /// </summary>
        public ResponseConfig? Response { get; init; }

        /// <summary>
        /// 获取TlsSniPattern
        /// </summary>
        /// <returns></returns>
        public TlsSniPattern GetTlsSniPattern()
        {
            if (this.TlsSni == false)
            {
                return Configuration.TlsSniPattern.None;
            }
            if (string.IsNullOrEmpty(this.TlsSniPattern))
            {
                return Configuration.TlsSniPattern.Domain;
            }
            return new TlsSniPattern(this.TlsSniPattern);
        } 
    }
}
