using System;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.14 P0】DoH 端点失败计数自检（去抖 + 衰减）。
    /// <para>
    /// 熔断阈值 4 的语义是"连续 4 次**独立**失败"。冷启动时 3~8 个域名 × A/AAAA
    /// 并发打向同一批端点，一次网络拥塞会让同批请求**同时**打满超时预算 ——
    /// 计数被这批回声刷满，端点被误判为"连续失败 4 次"而熔断，
    /// 随后全部解析回落到本网络下不可用的明文 DNS，请求整齐地卡满 25s。
    /// </para>
    /// <para>
    /// 但去抖不能把熔断削弱成"永不触发"，也不能放过真正的死端点，所以这里钉三条性质：
    /// 超时回声只算 1 次；快失败（连接被拒/TLS 错误）必须逐次计入；
    /// 持续故障仍必须在可接受的秒级时间内累计到阈值。
    /// </para>
    /// </summary>
    public static class DohEndpointSelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestFirstFailureCounts();
            TestConcurrentEchoDebounced();
            TestFastFailureIsNotDebounced();
            TestSpacedFailuresAccumulate();
            TestSustainedOutageStillTrips();
            TestFastFailuresTripImmediately();
            TestLongGapResets();
            TestClockSkewNotCounted();

            Console.WriteLine(failed == 0
                ? "  DohEndpointSelfTest: 全部通过"
                : $"  DohEndpointSelfTest: 失败 {failed} 项");
            return failed;
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

        private static readonly DateTime t0 = new(2026, 11, 15, 18, 20, 0, DateTimeKind.Utc);

        private static void TestFirstFailureCounts()
        {
            Console.WriteLine("首次失败计入");
            var (counted, failures) = DohResolver.EvaluateFailure(0, DateTime.MinValue, t0, slowFailure: true);
            Assert(counted && failures == 1, $"无历史时首败计入且为 1（实际 counted={counted}, failures={failures}）");
        }

        private static void TestConcurrentEchoDebounced()
        {
            Console.WriteLine("超时回声去抖");
            var (counted, failures) = DohResolver.EvaluateFailure(1, t0, t0.AddMilliseconds(120), slowFailure: true);
            Assert(counted == false && failures == 1, $"2s 内的重复超时不计入（实际 counted={counted}, failures={failures}）");

            var (counted2, _) = DohResolver.EvaluateFailure(1, t0, t0.AddSeconds(1.9d), slowFailure: true);
            Assert(counted2 == false, "临界 1.9s 仍不计入");
        }

        /// <summary>
        /// 快失败必须逐次计入：真死端点（连接被拒/证书错误）不能被去抖拖成"4 分钟才熔断"，
        /// 而这类失败本来就是"4 次请求 = 4 次独立证据"，不存在并发回声的歧义。
        /// </summary>
        private static void TestFastFailureIsNotDebounced()
        {
            Console.WriteLine("快失败不去抖");
            var (counted, failures) = DohResolver.EvaluateFailure(1, t0, t0.AddMilliseconds(50), slowFailure: false);
            Assert(counted && failures == 2, $"50ms 内的快失败照样计入（实际 counted={counted}, failures={failures}）");
        }

        private static void TestSpacedFailuresAccumulate()
        {
            Console.WriteLine("间隔失败累计");
            var (counted, failures) = DohResolver.EvaluateFailure(1, t0, t0.AddSeconds(2.1d), slowFailure: true);
            Assert(counted && failures == 2, $"2.1s 后的超时失败正常累计（实际 counted={counted}, failures={failures}）");
        }

        private static void TestSustainedOutageStillTrips()
        {
            Console.WriteLine("持续超时仍可熔断");
            var failures = 1;
            var last = t0;
            // 每 2.5 秒一次持续超时，模拟"真的挂了"的端点
            for (var i = 0; i < 3; i++)
            {
                var now = last.AddSeconds(2.5d);
                var (counted, next) = DohResolver.EvaluateFailure(failures, last, now, slowFailure: true);
                if (counted)
                {
                    failures = next;
                }
                last = now;
            }
            Assert(failures >= 4, $"约 7.5s 持续超时后达到熔断阈值 4（实际 {failures}）");
        }

        private static void TestFastFailuresTripImmediately()
        {
            Console.WriteLine("快失败立即熔断");
            var failures = 1;
            var last = t0;
            for (var i = 0; i < 3; i++)
            {
                var now = last.AddMilliseconds(80);
                var (counted, next) = DohResolver.EvaluateFailure(failures, last, now, slowFailure: false);
                if (counted)
                {
                    failures = next;
                }
                last = now;
            }
            Assert(failures >= 4, $"240ms 内 4 次快失败即达阈值（实际 {failures}）");
        }

        private static void TestLongGapResets()
        {
            Console.WriteLine("长时间间隔重置");
            var (counted, failures) = DohResolver.EvaluateFailure(3, t0, t0.AddMinutes(5d), slowFailure: true);
            Assert(counted && failures == 1, $"超过衰减窗口的失败重新从 1 计（实际 {failures}）");
        }

        private static void TestClockSkewNotCounted()
        {
            Console.WriteLine("时钟回拨防御");
            var (counted, failures) = DohResolver.EvaluateFailure(2, t0, t0.AddSeconds(-5d), slowFailure: true);
            Assert(counted == false && failures == 2, $"时间倒退视为同一故障窗口（实际 counted={counted}）");

            var (_, failures2) = DohResolver.EvaluateFailure(2, t0.AddSeconds(5d), t0, slowFailure: true);
            Assert(failures2 == 2, "负间隔不产生负数计数");
        }
    }
}
