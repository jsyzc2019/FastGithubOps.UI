using System.Net;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP健康度反馈通道
    /// <para>
    /// 移植自 dev-sidecar 的失败反馈闭环：真实请求的结果需要回流到IP选择逻辑，
    /// 否则坏IP会一直停留在「测速最快」的位置上。
    /// </para>
    /// <para>
    /// 之所以从 <see cref="IDomainResolver"/> 拆出来单独成接口：
    /// 反馈是「连接层」的事，而解析器还要服务 UI 的手动测速、SSH/Git 反代、刷新调度等
    /// 多种与健康度无关的调用方。让后者依赖一个只关心反馈的窄接口，
    /// 后续为健康度策略替换实现或写单测时不必牵扯解析器本身。
    /// </para>
    /// </summary>
    public interface IIpHealthFeedback
    {
        /// <summary>
        /// 上报一次连接成功
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
