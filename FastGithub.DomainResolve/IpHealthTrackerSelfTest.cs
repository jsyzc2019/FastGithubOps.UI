using System;
using System.Net;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IpHealthTracker 自检：确认"拉黑阈值/时长按候选池大小分级"这一 v2.6.9 修复真的生效。
    /// <para>
    /// 【为什么必须自检】这个缺陷的特征是"日志上一切正常，只是偶发一次卡 40 秒"，
    /// 外部表现与"网络不好"几乎无法区分。没有自检就无法确认分级真的在工作，
    /// 也无法防止将来某次重构把它悄悄改回去。
    /// </para>
    /// <para>
    /// 【实测背景】publish/v268/logs/log20261006.txt：
    /// 16:47:34 有 32 个请求在 20ms 内发起（GitHub 个人主页批量加载 participation 图），
    /// 其中 4 个 2.6s 完成、29 个卡 42~62s。GitHub 权威 DNS 对 github.com 只返1 条 A 记录，
    /// 候选池常态为 1；原KeepErrorThreshold=2 + BlacklistDuration=30s 意味着
    /// 一批并发里2 个失败就把唯一IP 拉黑 30 秒，在途请求全部走"全部拉黑→强制回退"重连。
    /// </para>
    /// </summary>
    public static class IpHealthTrackerSelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestSingleCandidateNeedsMoreFailures();
            TestMultiCandidateKeepsAggressiveThreshold();
            TestSingleCandidateBlacklistShorter();
            TestPoolSizeChangeTakesEffect();
            TestResetClearsPoolSize();
            TestSuccessAlwaysClearsBlacklist();
            TestFirstFailureNeverBlacklists();
            TestSustainedFailureStillBlacklists();

            Console.WriteLine(failed == 0
                ? "IpHealthTracker 自检全部通过"
                : $"IpHealthTracker 自检失败 {failed} 项");
            return failed;
        }

        private static readonly IPAddress ip = IPAddress.Parse("20.205.243.166");

        /// <summary>
        /// 核心回归：池=1 时连续失败 2 次**不得**拉黑（v2.6.8 的行为正是这里被修掉的）。
        /// </summary>
        private static void TestSingleCandidateNeedsMoreFailures()
        {
            var tracker = new IpHealthTracker();
            tracker.SetPoolSize("github.com", 1);

            for (var i = 1; i <= 2; i++)
            {
                tracker.ReportFailure("github.com", ip);
            }

            var blacklisted = tracker.IsBlacklisted("github.com", ip);
            if (blacklisted)
            {
                failed++;
                Console.WriteLine("  FAIL 池=1 时连续失败 2 次不应拉黑（唯一 IP 被拉黑= 域名瞬时不可用）");
            }
            else
            {
                Console.WriteLine("  OK   池=1 时连续失败 2 次不拉黑（保留了 2 次容错）");
            }

            // 第 4 次才该拉黑
            tracker.ReportFailure("github.com", ip);
            tracker.ReportFailure("github.com", ip);
            Assert(tracker.IsBlacklisted("github.com", ip), "池=1 时连续失败 4 次应拉黑");
        }

        /// <summary>
        /// 对照：池≥2 时必须保持原有的激进阈值 2（多候选拉黑一个只损失 1/2 冗余，代价可接受）。
        /// 若这条挂了，说明修复过度保守，会拖慢坏 IP 的让位速度。
        /// </summary>
        private static void TestMultiCandidateKeepsAggressiveThreshold()
        {
            var tracker = new IpHealthTracker();
            tracker.SetPoolSize("api.github.com", 2);

            tracker.ReportFailure("api.github.com", ip);
            tracker.ReportFailure("api.github.com", ip);

            Assert(tracker.IsBlacklisted("api.github.com", ip), "池=2 时连续失败 2 次应拉黑（保持原有阈值）");
        }

        /// <summary>
        /// 拉黑时长必须 ≥ 一次慢建连所需的时间。
        /// <para>
        /// 【v2.6.10 修订】本用例原先断言"池=1 的拉黑时长应**短于**多候选（10s vs 30s）"，
        /// v2.6.9 据此把池=1 压到 10s。但 v2.6.9 实测日志
        /// （publish/v269/logs/log20261007.txt）证明该方向过头：
        /// <c>camo.githubusercontent.com</c> 建连 p50=10.8s / p90=46.5s，
        /// 属"**建连本身就慢**"的域名 —— 10s 拉黑窗**短于一次正常握手**，
        /// 导致该 IP 被反复拉黑又反复放行，永远在抖动。
        /// </para>
        /// <para>
        /// 现在池=1 与池≥2 同为 30s，因此断言方向也随之改变：
        /// **不再要求池=1 更短**，只要求"拉黑窗 ≥ 慢建连可完成"这一硬下限。
        /// 真正的差异化保护由失败次数阈值承担（池=1 需 4 次、池≥2 需 2 次），
        /// 见 <see cref="TestSingleCandidateNeedsMoreFailures"/>。
        /// </para>
        /// </summary>
        private static void TestSingleCandidateBlacklistShorter()
        {
            var single = MeasureBlacklistSeconds(1);
            var multi = MeasureBlacklistSeconds(3);

            // 硬下限：拉黑窗必须 ≥ 20s，否则慢建连域名（camo p50=10.8s / p90=46.5s）
            // 的 IP 会在一次正常握手完成前就被拉黑并重新放行，形成抖动。
            if (single < 20 || multi < 20)
            {
                failed++;
                Console.WriteLine($"  FAIL 拉黑窗过短（单={single}s 多={multi}s），慢建连域名会反复抖动（期望 ≥20s）");
            }
            else
            {
                Console.WriteLine($"  OK   拉黑窗均 ≥ 慢建连耗时：池=1 → {single}s，池≥2 → {multi}s");
            }

            // 上限：仍需有界，避免真死 IP 长时间占据唯一候选。
            if (single > 60 || multi > 60)
            {
                failed++;
                Console.WriteLine($"  FAIL 拉黑时长越界：单={single}s 多={multi}s（期望 ≤60s）");
            }
            else
            {
                Console.WriteLine("  OK   拉黑时长均在合理上限内");
            }

            static double MeasureBlacklistSeconds(int poolSize)
            {
                var t = new IpHealthTracker();
                t.SetPoolSize("h", poolSize);
                // 连续失败足够多次以确保拉黑（池=1 需4 次，池≥2 需 2 次）
                for (var i = 0; i < 6; i++)
                {
                    t.ReportFailure("h", ip);
                }

                var snapshots = t.GetSnapshots(true, 10);
                foreach (var s in snapshots)
                {
                    if (s.Blacklisted)
                    {
                        return s.BlacklistRemainingSeconds;
                    }
                }
                return -1;
            }
        }

        /// <summary>
        /// 池大小变化后阈值必须跟着变（SetPoolSize 会同步到存量条目）。
        /// 这条对应真实场景：解析出的 IP 变多/变少时，惩罚力度应自动调整。
        /// </summary>
        private static void TestPoolSizeChangeTakesEffect()
        {
            // 先按池=1 建立状态并失败 2 次（不拉黑）
            var tracker = new IpHealthTracker();
            tracker.SetPoolSize("github.com", 1);
            tracker.ReportFailure("github.com", ip);
            tracker.ReportFailure("github.com", ip);

            // 池变大 → 阈值降到 2 → 第 3 次失败应立即拉黑
            tracker.SetPoolSize("github.com", 3);
            tracker.ReportFailure("github.com", ip);

            Assert(tracker.IsBlacklisted("github.com", ip), "池从 1 变 3 后，阈值应降至 2 并立即拉黑");
        }

        /// <summary>
        /// Reset 必须清掉池大小：域名 IP 列表整体变化后，
        /// 新的更大的 IP 集合不该沿用旧的"池=1"严阈值。
        /// </summary>
        private static void TestResetClearsPoolSize()
        {
            var tracker = new IpHealthTracker();
            tracker.SetPoolSize("github.com", 1);
            tracker.Reset("github.com");

            // Reset 后未回填池大小 → 按多候选处理（阈值 2）
            tracker.ReportFailure("github.com", ip);
            tracker.ReportFailure("github.com", ip);

            Assert(tracker.IsBlacklisted("github.com", ip), "Reset 后应回落到多候选阈值 2");
        }

        /// <summary>
        /// 无论池大小，**一次成功都必须立刻解除拉黑**。
        /// 这条是兜底安全网：若它在池=1 时失效，会直接复现"域名长时间不可用"。
        /// </summary>
        private static void TestSuccessAlwaysClearsBlacklist()
        {
            foreach (var poolSize in new[] { 1, 3 })
            {
                var tracker = new IpHealthTracker();
                tracker.SetPoolSize("h", poolSize);
                for (var i = 0; i < 6; i++)
                {
                    tracker.ReportFailure("h", ip);
                }

                Assert(tracker.IsBlacklisted("h", ip), $"池={poolSize} 时连续失败后应先处于拉黑态");
                tracker.ReportSuccess("h", ip);
                Assert(tracker.IsBlacklisted("h", ip) == false, $"池={poolSize} 时一次成功应立即解除拉黑");
            }
        }

        /// <summary>
        /// 回归既有缺陷：<b>第 1 次失败绝不能拉黑</b>。
        /// <para>
        /// 【自检首次运行时就是这里 FAIL 的】原写法 <c>successRate &lt; MinSuccessRate</c>
        /// 在第 1 次失败后就算出 successRate=0.0 →立即拉黑，
        /// 导致 <c>KeepErrorThreshold</c> 无论配 2 还是 4 都不生效。
        /// 这意味着实测中"唯一候选 IP 瞬间被拉黑"的真实触发点是<b>第 1 次失败</b>，
        /// 比原先判断的"第 2 次"更早。这条用例是防止它被改回去的核心护栏。
        /// </para>
        /// </summary>
        private static void TestFirstFailureNeverBlacklists()
        {
            foreach (var poolSize in new[] { 1, 2, 5 })
            {
                var tracker = new IpHealthTracker();
                tracker.SetPoolSize("h", poolSize);
                tracker.ReportFailure("h", ip);

                Assert(tracker.IsBlacklisted("h", ip) == false,
                    $"池={poolSize}：第 1 次失败不得拉黑（成功率样本不足，无统计意义）");
            }
        }

        /// <summary>
        /// 反向护栏：<b>真坏的 IP 必须仍然会被拉黑</b>，且不同池大小都能走到。
        /// <para>
        /// 【写这个用例时踩过的坑】第一版写成"失败与成功交替 30 轮"，
        /// 结果池=1 FAIL、池=3 PASS。查下来是**用例本身写错了**，不是代码回归：
        /// 交替场景下每次成功都会清零 <c>KeepErrorCount</c>，成功率也稳定在 ~0.75，
        /// 两个判定条件都不触发——而这种"偶尔成功"的 IP 本来就不该被拉黑。
        /// <para>
        /// 也就是说：改动前该场景靠 successRate 拉黑，改动后靠连续失败拉黑，
        /// 但交替场景两者都不该触发。**这正说明新判定是更保守、更正确的**。
        /// <para>
        /// 真正要防的矫枉过正是"真坏 IP 永远不被拉黑"，所以这里改用<b>纯持续失败</b>：
        /// 池=1 需累计 4 次连续失败、池≥2 需 2 次，都必须走到拉黑。
        /// </para>
        /// </summary>
        private static void TestSustainedFailureStillBlacklists()
        {
            // 纯持续失败：不含任何成功，累计到阈值必须拉黑
            foreach (var poolSize in new[] { 1, 3 })
            {
                var tracker = new IpHealthTracker();
                tracker.SetPoolSize("h", poolSize);

                var rounds = 0;
                while (tracker.IsBlacklisted("h", ip) == false && rounds < 30)
                {
                    tracker.ReportFailure("h", ip);
                    rounds++;
                }

                var expected = poolSize == 1 ? 4 : 2;
                Assert(tracker.IsBlacklisted("h", ip),
                    $"池={poolSize}：持续失败必须被拉黑（健康度闭环不能失效）");
                Assert(rounds == expected,
                    $"池={poolSize}：应在第 {expected} 次失败时拉黑，实际第 {rounds} 次");
            }

            // 反向：偶尔成功的 IP **不应**被拉黑（避免矫枉过正）
            var mixed = new IpHealthTracker();
            mixed.SetPoolSize("h", 1);
            for (var i = 0; i < 30; i++)
            {
                mixed.ReportFailure("h", ip);
                if (i % 3 == 2)
                {
                    mixed.ReportSuccess("h", ip);
                }
            }

            Assert(mixed.IsBlacklisted("h", ip) == false,
                "偶尔成功的 IP 不应被拉黑（失败与成功交替 30 轮）");
        }

        private static void Assert(bool condition, string message)
        {
            if (condition)
            {
                Console.WriteLine($"  OK   {message}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {message}");
            }
        }
    }
}
