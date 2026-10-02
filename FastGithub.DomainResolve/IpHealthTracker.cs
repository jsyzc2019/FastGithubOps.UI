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

        /// <summary>
        /// 统计值衰减系数。
        /// <para>
        /// 每次上报前把 <see cref="IpState.Total"/> 与 <see cref="IpState.Error"/> 各乘以该系数，
        /// 使成功率成为「近N次」的有效统计而不是生命周期累计值。
        /// </para>
        /// <para>
        /// 若不衰减，一次偶发失败（例如 TLS 握手超时）会让该IP的惩罚值永久居高不下，
        /// 由于排序是 <c>OrderBy</c> 字典序（惩罚项优先于时延项），
        /// 好的IP会被永久排到后面，排序逐渐退化成「偏好最新出现的IP」。
        /// </para>
        /// </summary>
        private const double DECAY = 0.9d;

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
        /// 连续失败达到该次数即拉黑。
        /// <para>
        /// dev-sidecar 取 1，但那是「宁可立刻换IP」的激进策略；
        /// 本项目默认放宽到 2，避免单次网络抖动（WiFi 瞬断、网关重启）
        /// 直接把一个域名的全部候选IP一次性拉黑。
        /// </para>
        /// </summary>
        public int KeepErrorThreshold { get; set; } = 2;

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
                // 先衰减历史统计，让本次成功能逐步抵销过去的失败
                Decay(state);
                state.Total++;
                state.KeepErrorCount = 0;
                state.LastAccess = DateTime.UtcNow;
                // 成功后立即解除拉黑，让好IP尽快回到可用列表
                state.BlacklistUntil = DateTime.MinValue;
            }
            this.TrimIfRequired();
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
                Decay(state);
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
        /// 统计值衰减：让成功率成为「近N次」的有效统计而非生命周期累计值
        /// </summary>
        private static void Decay(IpState state)
        {
            state.Total = (int)(state.Total * DECAY);
            state.Error = (int)(state.Error * DECAY);
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

            // 按最久未访问清理，不排除正在拉黑的条目。
            // 失败风暴时绝大多数条目都处于拉黑状态，若按「未拉黑」过滤，
            // trim 恰好会在最需要它的时候什么都不删，状态表会持续膨胀。
            var victims = this.states
                .OrderBy(item => item.Value.LastAccess)
                .Take(MAX_STATES / 4)
                .Select(item => item.Key)
                .ToArray();

            foreach (var key in victims)
            {
                this.states.TryRemove(key, out _);
            }
        }
    }
}
