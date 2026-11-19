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
        /// git 智能 HTTP 协议请求的预算。
        /// <para>
        /// git clone/pull/push 走 <c>info/refs</c> + <c>git-upload-pack</c> / <c>git-receive-pack</c>，
        /// 大 pack 在跨境链路（约 20 KB/s）下需数分钟传输，而域名默认的整体预算
        /// （github.com = 25s）会在传输中途用 CancellationToken 砍断连接，
        /// 表现为 <c>unexpected disconnect while reading sideband packet</c> /
        /// <c>invalid index-pack output</c>。给 git 请求一个独立、宽松的预算，
        /// 普通请求仍保持短超时快速失败。
        /// </para>
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
        /// 给出实际生效的请求预算：遥测请求最多 8 秒，git 智能协议请求放宽到 10 分钟，
        /// 其余按域名配置原样返回。
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
            if (IsTelemetryBeacon(uri))
            {
                return configured.HasValue && configured.Value <= TelemetryTimeout
                    ? configured
                    : TelemetryTimeout;
            }

            // git 智能协议：放宽到 GitTimeout（若域名配置更大则保留配置），
            // 避免大 pack 传输被域名默认整体预算中途砍断。
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
