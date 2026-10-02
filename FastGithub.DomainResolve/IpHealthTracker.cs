using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP健康度跟踪器
    /// <para>
    /// 移植并适配自 dev-sidecar 的 <c>DynamicChoice</c>（packages/mitmproxy/src/lib/choice/index.js）。
    /// </para>
    /// <para>
    /// 原 FastGithub 只有"测速时延"这一个维度：某个IP一旦被判定为快，就会在其缓存有效期内
    /// 被持续优先选中，即使它实际上已经连不通了（TLS层被阻断时TCP握手仍可能成功，
    /// 或者请求已经失败但没有任何反馈通道把它标记为坏）。
    /// 这里补上反馈闭环：统计每个IP的成功率与连续失败次数，连续失败达到阈值或成功率过低时
    /// 立即将其拉黑，并在排序时降低其权重。
    /// </para>
    /// </summary>
    public sealed class IpHealthTracker
    {
        /// <summary>
        /// 状态上限，超出后清理最久未使用的条目
        /// </summary>
        private const int MAX_STATES = 4096;

        private sealed class IpState
        {
            public int Total;
            public int Error;
            public int KeepErrorCount;
            public DateTime BlacklistUntil = DateTime.MinValue;
            public DateTime LastAccess = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, IpState> states = new();

        /// <summary>
        /// 连续失败达到该次数即拉黑（dev-sidecar 取 1：坏IP不值得让用户多等几轮超时）
        /// </summary>
        public int KeepErrorThreshold { get; set; } = 1;

        /// <summary>
        /// 成功率低于该值即拉黑
        /// </summary>
        public double MinSuccessRate { get; set; } = 0.4d;

        /// <summary>
        /// 拉黑时长
        /// </summary>
        public TimeSpan BlacklistDuration { get; set; } = TimeSpan.FromMinutes(2d);

        /// <summary>
        /// 获取key
        /// </summary>
        private static string GetKey(string host, IPAddress address) => $"{host}|{address}";

        /// <summary>
        /// 上报一次成功
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="address">IP</param>
        public void ReportSuccess(string host, IPAddress address)
        {
            var state = this.states.GetOrAdd(GetKey(host, address), _ => new IpState());
            lock (state)
            {
                state.Total++;
                state.KeepErrorCount = 0;
                state.LastAccess = DateTime.UtcNow;
                // 成功后立即解除拉黑，让好IP尽快回到可用列表
                state.BlacklistUntil = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 上报一次失败
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="address">IP</param>
        public void ReportFailure(string host, IPAddress address)
        {
            var state = this.states.GetOrAdd(GetKey(host, address), _ => new IpState());
            lock (state)
            {
                state.Total++;
                state.Error++;
                state.KeepErrorCount++;
                state.LastAccess = DateTime.UtcNow;

                var successRate = 1d - (double)state.Error / state.Total;
                if (state.KeepErrorCount >= this.KeepErrorThreshold || successRate < this.MinSuccessRate)
                {
                    state.BlacklistUntil = DateTime.UtcNow + this.BlacklistDuration;
                }
            }

            this.TrimIfRequired();
        }

        /// <summary>
        /// 是否已被拉黑
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="address">IP</param>
        /// <returns></returns>
        public bool IsBlacklisted(string host, IPAddress address)
        {
            if (this.states.TryGetValue(GetKey(host, address), out var state) == false)
            {
                return false;
            }

            lock (state)
            {
                return state.BlacklistUntil > DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 获取成功率（无记录时返回1，表示未知即视为可用）
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="address">IP</param>
        /// <returns></returns>
        public double GetSuccessRate(string host, IPAddress address)
        {
            if (this.states.TryGetValue(GetKey(host, address), out var state) == false)
            {
                return 1d;
            }

            lock (state)
            {
                if (state.Total <= 0)
                {
                    return 1d;
                }
                return 1d - (double)state.Error / state.Total;
            }
        }

        /// <summary>
        /// 排序权重：成功率越低权重越大（排越靠后）
        /// 与dev-sidecar一致，健康度优先于时延——一个"快但连不上"的IP毫无价值
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="address">IP</param>
        /// <returns></returns>
        public double GetPenalty(string host, IPAddress address)
        {
            var rate = this.GetSuccessRate(host, address);
            if (rate >= 1d)
            {
                return 0d;
            }
            // 成功率 0.5 → 权重 1；成功率 0 → 权重 4
            return (1d - rate) * 4d;
        }

        /// <summary>
        /// 清理指定域名的统计（域名IP列表发生整体变化时调用）
        /// </summary>
        /// <param name="host">域名</param>
        public void Reset(string host)
        {
            var prefix = $"{host}|";
            foreach (var key in this.states.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                this.states.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 超出上限时清理最久未访问的条目
        /// </summary>
        private void TrimIfRequired()
        {
            if (this.states.Count <= MAX_STATES)
            {
                return;
            }

            var expired = this.states
                .Where(item => item.Value.BlacklistUntil <= DateTime.UtcNow)
                .OrderBy(item => item.Value.LastAccess)
                .Take(MAX_STATES / 4)
                .Select(item => item.Key)
                .ToArray();

            foreach (var key in expired)
            {
                this.states.TryRemove(key, out _);
            }
        }
    }
}
