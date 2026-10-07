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

            /// <summary>
            /// 【v2.6.9】该域名当前的候选池大小（由 IPAddressService 在每次选候选时回填）。
            /// <para>
            /// 0 表示"尚未知"。它决定 <see cref="EffectiveKeepErrorThreshold"/> 的取值：
            /// 池子只有1 个 IP 时，连续失败 2 次就拉黑，等于**该域名瞬间不可用**，
            /// 此时 <c>MAX_TRY_COUNT=3</c> 与总预算切片全部失效（无可试候选）。
            /// </para>
            /// </summary>
            public int PoolSize;
        }

        private readonly ConcurrentDictionary<string, IpState> states = new();

        /// <summary>
        /// 各域名当前候选池大小。由 <see cref="IPAddressService.GetAddressesAsync"/> 每次选出候选后回填。
        /// </summary>
        private readonly ConcurrentDictionary<string, int> poolSizes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 候选池只有这一个 IP 时，连续失败多少次才拉黑。
        /// <para>
        /// 【v2.6.9 为什么需要这一档】实测 GitHub 权威 DNS 对 <c>github.com</c>
        /// 只返 1 条A 记录（616 次 DoH 解析中 266 次"得 1 个候选"，其中 github.com 占 29 次），
        /// 所以它的候选池常态就是 1。此时「一批并发请求里只要有 <b>1 个</b>失败，
        /// 唯一的 IP 就被拉黑（原因见下方 <see cref="MinSamplesBeforeRate"/>的说明），
        /// 而那批已在途的请求全部被迫走"全部拉黑 → 强制回退"重连。
        /// 实测后果：32 个并发请求里 4 个 2.6s 完成、29 个卡 42～62 秒，
        /// 页面表现为"打开个人主页卡 40 秒后正常显示"。
        /// 提到 4 相当于给了 4 次容错，够挡住瞬时抖动，又不会让单 IP 域名被一次波动打空。
        /// </para>
        /// </summary>
        private const int SingleCandidateKeepErrorThreshold = 4;

        /// <summary>
        /// 候选池只有这一个 IP 时的拉黑时长。
        /// <para>
        /// 【v2.6.10 为什么从 10s 回调到 30s】v2.6.9 把它从 30s 压到 10s，
        /// 目的是缩小"单 IP 域名被拉黑＝整域名瞬时不可用"的窗口。
        /// 但 v2.6.9 实测日志（publish/v269/logs/log20261007.txt）证明这个方向过头了：
        /// <c>camo.githubusercontent.com</c> 的建连 p50=**10.8s**、p90=**46.5s**
        /// （&gt;10s 占 51.2%、&gt;30s 占 21.6%）——它属于"**建连本身就慢**"的域名。
        /// 慢建连的失败与"IP 已死"是两回事：TCP+TLS 握手本来就要十几秒，
        /// 而 10s 拉黑窗远短于一次正常建连，导致
        /// <list type="bullet">
        /// <item>IP 被拉黑 10s → 刚过冷却期立刻又被判定失败 → 反复拉黑，
        /// 该 IP 实际上处于"永远在拉黑与放行之间抖动"的状态；</item>
        /// <item>而用户侧看到的是 camo 图片 p50 10.8s 的加载卡顿。</item>
        /// </list>
        /// 实测"在 10s 内未能连接"（总预算耗尽）141 次，其中
        /// "已尝试 1/3 个候选" 29 次、"1/2" 17 次 —— 池子明明有多个候选，
        /// 却在第 1 个候选耗尽总预算后直接 break，候选 2、3 根本没被试。
        /// <para>
        /// 因此回调到 30s（与池 ≥2 对齐）：让慢建连的IP 有足够时间完成一次正常握手，
        /// 而不是被反复拉黑。对比 v2.6.8 之前的 30s 也不会退化为"恢复慢"——
        /// 真正的兜底是"全部拉黑时强制回退重试"，且 v2.6.9 已把失败次数阈值放宽到 4 次。
        /// </para>
        /// </summary>
        private static readonly TimeSpan SingleCandidateBlacklistDuration = TimeSpan.FromSeconds(30d);

        /// <summary>
        /// 候选池大于1 时的拉黑时长。
        /// </summary>
        private static readonly TimeSpan MultiCandidateBlacklistDuration = TimeSpan.FromSeconds(30d);

        /// <summary>
        /// 连续失败达到该次数即拉黑（候选池 ≥ 2 时生效）。
        /// <para>
        /// 取 2（此前为 3）：本参数与<a cref="BlacklistDuration"/>、以及 DomainResolver 的
        /// REFRESH_COOLDOWN 是**串联**关系，一个坏 IP 从"首次失败"到"被彻底让位"的总时延
        /// 约等于 KeepErrorThreshold × 单次失败耗时 + BlacklistDuration + REFRESH_COOLDOWN。
        /// 原值 3+ 5min+ 30s 意味着被阻断后要等数分钟才换IP——这正是"恢复速度不够快"的直接原因。
        /// 降到 2 让坏 IP 更快让位，同时仍高于 dev-sidecar 的 1，保留一次容错避免网络瞬断即误杀。
        /// 池子 ≥2 时拉黑一个只损失 1/2冗余，代价可接受。
        /// </para>
        /// </summary>
        public int KeepErrorThreshold { get; set; } = 2;

        /// <summary>
        /// 记录某域名当前的候选池大小，影响后续的拉黑阈值与时长。
        /// <para>
        /// 由 <see cref="IPAddressService.GetAddressesAsync"/> 在选出候选后调用。
        /// 池大小是"惩罚代价"的放大系数：池 ≥3 时拉黑一个只少1/3 冗余，
        /// 池 =1 时拉黑等于域名不可用 —— 两者必须用不同的阈值。
        /// </para>
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="poolSize">候选IP 数量，至少为 1</param>
        public void SetPoolSize(string host, int poolSize)
        {
            if (poolSize < 1)
            {
                poolSize = 1;
            }

            this.poolSizes[host] = poolSize;

            // 同步到已有条目：池子从 ≥2 缩到 1（或反之）时，让存量条目下次判定即生效。
            var prefix = $"{host}|";
            foreach (var item in this.states)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal) == false)
                {
                    continue;
                }

                lock (item.Value)
                {
                    item.Value.PoolSize = poolSize;
                }
            }
        }

        /// <summary>
        /// 取该IP 当前生效的连续失败阈值（按候选池大小分级）。
        /// </summary>
        private int GetKeepErrorThreshold(IpState state, string host)
        {
            // 优先用条目自身记录的池大小（反映最近一次选候选时的真实情况），
            // 回退到域名级的最新值。两者都没有则按多候选处理（保守= 不轻易拉黑）。
            var poolSize = state.PoolSize > 0
                ? state.PoolSize
                : (this.poolSizes.TryGetValue(host, out var size) ? size : 0);

            return poolSize == 1 ? SingleCandidateKeepErrorThreshold : this.KeepErrorThreshold;
        }

        /// <summary>
        /// 取该IP 当前生效的拉黑时长（按候选池大小分级）。
        /// </summary>
        private static TimeSpan GetBlacklistDuration(IpState state)
        {
            return state.PoolSize == 1
                ? SingleCandidateBlacklistDuration
                : MultiCandidateBlacklistDuration;
        }

        /// <summary>
        /// 成功率低于该值即拉黑
        /// <para>
        /// 【v2.6.9】仅在样本数达到 <see cref="MinSamplesBeforeRate"/> 后才生效，
        /// 否则第1 次失败就会算出successRate=0 并立刻拉黑，
        /// 使 <see cref="KeepErrorThreshold"/> 完全失效。
        /// </para>
        /// </summary>
        public double MinSuccessRate { get; set; } = 0.4d;

        /// <summary>
        /// 允许成功率参与拉黑判定所需的最小样本数。
        /// <para>
        /// 统计量在样本不足时不可靠：1 次失败就会得到成功率 0.0，
        /// 若直接与 <see cref="MinSuccessRate"/>（0.4）比较，任何 IP 都会在首次失败后被拉黑。
        /// 这里取 6：既能积累出"确实在持续失败"的证据（成功率会稳定低于 0.4），
        /// 又不至于让"偶发一两次失败"就误杀。样本期内的保护由连续失败阈值负责。
        /// </para>
        /// </summary>
        private const int MinSamplesBeforeRate = 6;

        /// <summary>
        /// 拉黑时长（候选池 ≥ 2 时生效）。
        /// <para>
        /// 取 30 秒（原 5 分钟，v2.5.4 曾从 2 分钟上调到 5 分钟以抑制抖动）。
        /// 【为什么又调回来】用户实测反馈"被阻断后恢复速度不够快"，而拉黑时长是这条链路上
        /// 最长的一环：拉黑期内该IP 既不参与排序、又会在探测时被跳过，
        /// 若这段时间内没有新IP 可用，该域名就只有一个被拉黑的候选，等于事实不可用。
        ///<para>
        /// 抖动其实是上一次上调想解决的问题，但它的正确解法是"IP 稳定"（DoH 缓存 10 分钟 +
        /// 粘性单次握手），而不是"把坏 IP 关很久"——关很久正好牺牲了恢复速度。
        /// 这里取 30 秒：足以让坏 IP 让位并被新IP 顶替，又不会让一个仅短暂抖动的 IP 被长时间丢弃。
        /// 池 ≥2 时还有其它候选可试，被拉黑的IP 很快会被顶替。
        /// <para>
        /// <b>【v2.6.9】单IP 候选的域名（池=1）改用更短的
        /// <see cref="SingleCandidateBlacklistDuration"/>（10 秒）</b>，
        /// 因为此时拉黑等于整个域名不可用，没有"其它候选可试"这个缓冲。
        /// 真正的兜底是"全部拉黑时强制回退重试"（见 IPAddressService 的 candidates 回落逻辑），
        /// 因此即便误拉黑也不会导致域名永久不可用。
        /// </para>
        /// </summary>
        public TimeSpan BlacklistDuration { get; set; } = MultiCandidateBlacklistDuration;

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

                // 池大小首次使用时从域名级取值补齐（SetPoolSize 之后新增的 IP 也要补）。
                if (state.PoolSize == 0 && this.poolSizes.TryGetValue(host, out var size))
                {
                    state.PoolSize = size;
                }

                var successRate = 1d - (double)state.Error / state.Total;
                var threshold = this.GetKeepErrorThreshold(state, host);

                //【v2.6.9 关键修正】successRate 判定必须带"最小样本数"门槛。
                //
                // 【为什么必须修 —— 自检实测发现的既有缺陷，之前无人察觉】
                // 原写法 `successRate < MinSuccessRate`（0.4）在**第 1 次失败**时就成立：
                //   第1次失败后 Total=1, Error=1 → successRate = 0.0 < 0.4 → 立即拉黑。
                // 也就是说 <see cref="KeepErrorThreshold"/>（连续失败次数）**从未真正生效过**，
                // 无论它配 2 还是 4，都会被 successRate 这条路径抢先触发。
                // 这解释了实测中"一批并发里唯一 IP 瞬间就被拉黑"——
                // 真实触发点是**第 1 次失败**，比原先判断的"第 2 次"更早、更容易。
                //
                // 【修法】成功率是统计量，样本不足时没有统计意义。
                // 要求至少 MIN_SAMPLES_BEFORE_RATE 样本才允许它参与判定，
                // 小样本期一律只看连续失败次数 —— 这才是KeepErrorThreshold 的设计本意。
                if (state.KeepErrorCount >= threshold
                    || (state.Total >= MinSamplesBeforeRate && successRate < this.MinSuccessRate))
                {
                    state.BlacklistUntil = DateTime.UtcNow + GetBlacklistDuration(state);
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

            // 【v2.6.9】域名IP 列表整体变化时，旧的候选池大小已不再成立。
            // 若不清理，新IP 集合可能变大（阈值该按多候选走）却仍沿用旧的"池=1"严阈值，
            // 导致新IP 被无故快速拉黑。
            this.poolSizes.TryRemove(host, out _);
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
        /// 获取全部状态的只读快照。
        /// <para>
        /// 修复闭环加上了，但如果看不到内部状态就无法确认它是否真的在工作：
        /// 本轮的实际情况是"IP健康度"一旦静默失效，外部表现和"网络不好"完全一样。
        /// 这里对外暴露快照，供诊断端点与日志使用。
        /// </para>
        /// </summary>
        /// <param name="includeHealthy">是否包含健康条目（false 时只返回被拉黑或有过失败的）</param>
        /// <param name="maxCount">最多返回多少条</param>
        /// <returns></returns>
        public IReadOnlyList<IpHealthSnapshot> GetSnapshots(bool includeHealthy = true, int maxCount = 200)
        {
            var now = DateTime.UtcNow;
            var list = new List<IpHealthSnapshot>(Math.Min(this.states.Count, maxCount));

            foreach (var item in this.states)
            {
                var state = item.Value;
                int total, error, keepErrorCount;
                DateTime blacklistUntil;
                lock (state)
                {
                    total = state.Total;
                    error = state.Error;
                    keepErrorCount = state.KeepErrorCount;
                    blacklistUntil = state.BlacklistUntil;
                }

                var blacklisted = blacklistUntil > now;
                if (includeHealthy == false && blacklisted == false && error == 0)
                {
                    continue;
                }

                var separator = item.Key.LastIndexOf('|');
                var host = separator > 0 ? item.Key[..separator] : item.Key;
                var address = separator > 0 ? item.Key[(separator + 1)..] : string.Empty;

                list.Add(new IpHealthSnapshot
                {
                    Host = host,
                    Address = address,
                    Total = total,
                    Error = error,
                    KeepErrorCount = keepErrorCount,
                    SuccessRate = total > 0 ? 1d - (double)error / total : 1d,
                    Blacklisted = blacklisted,
                    BlacklistRemainingSeconds = blacklisted ? (int)Math.Ceiling((blacklistUntil - now).TotalSeconds) : 0
                });
            }

            return list
                .OrderByDescending(item => item.Blacklisted)
                .ThenBy(item => item.SuccessRate)
                .ThenBy(item => item.Host, StringComparer.Ordinal)
                .Take(maxCount)
                .ToArray();
        }

        /// <summary>
        /// 获取状态条目总数
        /// </summary>
        public int Count => this.states.Count;

        /// <summary>
        /// 获取当前被拉黑的条目数
        /// </summary>
        /// <returns></returns>
        public int GetBlacklistCount()
        {
            var now = DateTime.UtcNow;
            var count = 0;
            foreach (var state in this.states.Values)
            {
                lock (state)
                {
                    if (state.BlacklistUntil > now)
                    {
                        count++;
                    }
                }
            }
            return count;
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
