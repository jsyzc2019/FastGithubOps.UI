using System;

namespace FastGithub.Http
{
    /// <summary>
    /// 【v2.6.14 P1】遥测上报请求的超时策略。
    /// <para>
    /// GitHub 的页面会持续发送统计 beacon（<c>collector.github.com/github/collect</c>、
    /// <c>api.github.com/_private/browser/rum|stats</c>）。它们对**用户无任何价值**：
    /// 浏览器不等结果、失败也不影响页面。但当前配置会让它们吃满域名的整体预算
    /// （api.github.com = 25s，collector 走 *.github.com = 30s）。
    /// </para>
    /// <para>
    /// 11-15 实测：504 里有 66%（92/139）正是这三个端点，且都是 25001ms 整 ——
    /// 也就是说故障窗口内它们持续占用连接与线程，把真正有用的请求挤在队列里。
    /// 给它们 8 秒的 fail-fast 预算，收益是"故障时不再放大排队"，代价是统计报表少几条
    /// 本来就会丢的 beacon。
    /// </para>
    /// </summary>
    public static class TelemetryPolicy
    {
        /// <summary>
        /// 遥测 beacon 的请求预算
        /// </summary>
        public static readonly TimeSpan TelemetryTimeout = TimeSpan.FromSeconds(8d);

        /// <summary>
        /// 整域都是遥测的域名
        /// </summary>
        private static readonly string[] telemetryHosts =
        {
            "collector.github.com",
            "collector.githubapp.com",
            "collector.cdn.githubapp.com",
        };

        /// <summary>
        /// api.github.com 上的遥测路径前缀
        /// </summary>
        private const string browserTelemetryPathPrefix = "/_private/browser/";

        /// <summary>
        /// 是否为遥测上报请求（纯函数，由 <see cref="TelemetryPolicySelfTest"/> 钉死）
        /// </summary>
        /// <param name="uri">转发前的原始请求地址</param>
        /// <returns></returns>
        public static bool IsTelemetryBeacon(Uri uri)
        {
            if (uri == null || uri.IsAbsoluteUri == false)
            {
                return false;
            }

            foreach (var host in telemetryHosts)
            {
                if (string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return uri.AbsolutePath.StartsWith(browserTelemetryPathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 给出实际生效的请求预算：遥测请求最多 8 秒，其余按域名配置原样返回。
        /// <para>
        /// 只收紧、不放松：域名已配置了更小的超时（如 5s）时保留配置值，
        /// 避免"策略"反而把预算调大。
        /// </para>
        /// </summary>
        /// <param name="uri">请求地址</param>
        /// <param name="configured">域名配置的预算，null 表示未配置</param>
        /// <returns></returns>
        public static TimeSpan? LimitTimeout(Uri uri, TimeSpan? configured)
        {
            if (IsTelemetryBeacon(uri) == false)
            {
                return configured;
            }

            if (configured.HasValue && configured.Value <= TelemetryTimeout)
            {
                return configured;
            }

            return TelemetryTimeout;
        }
    }
}
