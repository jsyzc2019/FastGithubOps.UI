using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary>
    public interface IDomainResolver
    { 
        /// <summary>
        /// 解析所有ip
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        IAsyncEnumerable<IPAddress> ResolveAsync(DnsEndPoint endPoint, CancellationToken cancellationToken = default);

        /// <summary>
        /// 对所有节点进行测速
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task TestSpeedAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新所有域名的IP（清空缓存并重新解析测速）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task RefreshAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 刷新所有域名的IP（仅使用在线hosts源，不发起DNS查询）
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task RefreshHostsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 上报一次连接成功
        /// <para>
        /// 移植自 dev-sidecar 的失败反馈闭环：真实请求的结果需要回流到IP选择逻辑，
        /// 否则坏IP会一直停留在"测速最快"的位置上。
        /// </para>
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">实际使用的IP</param>
        void ReportSuccess(DnsEndPoint endPoint, IPAddress address);

        /// <summary>
        /// 上报一次连接失败
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">实际使用的IP</param>
        void ReportFailure(DnsEndPoint endPoint, IPAddress address);

        /// <summary>
        /// 该IP是否已被判定为不可用
        /// </summary>
        /// <param name="endPoint">节点</param>
        /// <param name="address">IP</param>
        /// <returns></returns>
        bool IsBlacklisted(DnsEndPoint endPoint, IPAddress address);
    }
}