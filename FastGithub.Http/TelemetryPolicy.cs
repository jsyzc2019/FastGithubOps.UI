using System;

namespace FastGithub.Http
{
    /// <summary>
    /// 【v2.6.14 P1】请求超时策略：遥测 fail-fast + git 智能协议宽松预算。
    /// <para>
    /// 遥测上报（beacon）：GitHub 的页面会持续发送统计 beacon（<c>collector.github.com/github/collect</c>、
    /// <c>api.github.com/_private/browser/rum|stats</c>）。它们对**用户无任何价值**：
    /// 浏览器不等结果、失败也不影响页面。但当前配置会让它们吃满域名的等待预算
    /// （api.github.com = 25s，collector 走 *.github.com = 30s）。
    /// </para>
    /// <para>
    /// 11-15 实测：504 里有 66%（92/139）正是这三个端点，且都是 25001ms 整 ——
    /// 也就是说故障窗口内它们持续占用连接与线程，把真正有用的请求挤在队列里。
    /// 给它们 8 秒的 fail-fast 预算，收益是"故障时不再放大排队"，代价是统计报表少几条
    /// 本来就会丢的 beacon。
    /// </para>
    /// <para>
    /// Git 智能协议（fork 改动）：服务端准备 pack 时可能较慢。
    /// <c>info/refs</c>、<c>git-upload-pack</c> 和 <c>git-receive-pack</c> 独立放宽为
    /// <see cref="GitTimeout"/>（10 分钟）。该预算持续到收到上游响应头，正文转发由 YARP 管理。
    /// </para>
    /// </summary>
    public static class TelemetryPolicy
    {
        /// <summary>
        /// 遥测 beacon 的请求预算
        /// </summary>
        public static readonly TimeSpan TelemetryTimeout = TimeSpan.FromSeconds(8d);

        /// <summary>
        /// Git 智能 HTTP 请求从建连到收到上游响应头的默认预算。
        /// </summary>
        public static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10d);

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
        /// git 智能 HTTP 协议的路径后缀
        /// </summary>
        private static readonly string[] gitSmartHttpPathSuffixes =
        {
            "/info/refs",
            "/git-upload-pack",
            "/git-receive-pack",
        };

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
        /// 是否为 git 智能 HTTP 协议请求（纯函数，可由 <see cref="TelemetryPolicySelfTest"/> 钉死）
        /// </summary>
        /// <param name="uri">转发前的原始请求地址</param>
        /// <returns></returns>
        public static bool IsGitSmartHttpRequest(Uri uri)
        {
            if (uri == null || uri.IsAbsoluteUri == false)
            {
                return false;
            }

            var path = uri.AbsolutePath;
            foreach (var suffix in gitSmartHttpPathSuffixes)
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 给出等待上游响应头的请求预算。遥测请求最多 8 秒，Git 智能协议请求至少 10 分钟，
        /// 其余使用域名配置。
        /// <para>
        /// 遥测请求优先保留域名配置中的更短时限。
        /// </para>
        /// </summary>
        /// <param name="uri">请求地址</param>
        /// <param name="configured">域名配置的预算，null 表示未配置</param>
        /// <returns></returns>
        public static TimeSpan? LimitTimeout(Uri uri, TimeSpan? configured)
        {
            if (IsTelemetryBeacon(uri))
            {
                return configured.HasValue && configured.Value <= TelemetryTimeout
                    ? configured
                    : TelemetryTimeout;
            }

            // Git 智能协议等待上游响应头时，保留更宽的请求预算。
            if (IsGitSmartHttpRequest(uri))
            {
                return configured.HasValue && configured.Value > GitTimeout
                    ? configured
                    : GitTimeout;
            }

            return configured;
        }
    }
}
