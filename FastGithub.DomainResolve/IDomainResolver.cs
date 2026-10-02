using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary>
    public interface IDomainResolver : IIpHealthFeedback
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
        /// 获取IP健康度快照。
        /// 没有这个视图时，健康度反馈是否生效无法从外部判断——
        /// 失效的表现和"网络本来就差"完全一样。
        /// </summary>
        /// <param name="includeHealthy">是否包含健康条目</param>
        /// <param name="maxCount">最大条数</param>
        /// <returns></returns>
        IReadOnlyList<IpHealthSnapshot> GetIpHealth(bool includeHealthy = true, int maxCount = 200);
    }
}