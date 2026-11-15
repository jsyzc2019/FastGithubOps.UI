using System;
using System.Linq;
using System.Net;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.13】候选池策略自检（借鉴上游 v2.4.2 / v2.4.4 / v2.5.0 的 A1/A2/A4）。
    /// <para>
    /// 这些机制都是"函数对但调用路径断掉"或"参数被改回去"就容易静默失效的类型：
    /// 覆盖 vs 合并、成功 IP 是否置顶、测速周期是否被改回 1 秒，
    /// 失效后的表现都只是"网络时好时坏"，没有断言几乎无法定位。
    /// </para>
    /// </summary>
    public static class CandidatePolicySelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestTestPeriodNotTooAggressive();
            TestMergeKeepsOldCandidates();
            TestMergeDedupAndOrder();
            TestMergeRespectsLimit();
            TestMergeEmptyOld();
            TestReorderSuccessIp();
            TestReorderUnknownIp();

            Console.WriteLine(failed == 0
                ? "CandidatePolicySelfTest: 全部通过"
                : $"CandidatePolicySelfTest: 失败 {failed} 项");
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

        private static IPAddress Ip(string s) => IPAddress.Parse(s);

        /// <summary>
        /// 测速周期不得回到 1 秒级别（本仓日志实证：1 秒周期自造 DoH/明文 DNS 压力）
        /// </summary>
        private static void TestTestPeriodNotTooAggressive()
        {
            Console.WriteLine("测速周期下界");
            Assert(DomainResolveHostedService.TEST_PERIOD_SECONDS >= 30,
                $"测速周期 {DomainResolveHostedService.TEST_PERIOD_SECONDS}s 不低于 30s（原 1s 已被证伪）");
        }

        /// <summary>
        /// 核心断言：刷新只探到 1 个 IP 时，候选池不得塌缩为单点
        /// </summary>
        private static void TestMergeKeepsOldCandidates()
        {
            Console.WriteLine("候选合并：不塌缩");
            var fresh = new[] { Ip("20.205.243.166") };
            var old = new[] { Ip("140.82.121.3"), Ip("20.26.156.215"), Ip("140.82.113.3") };
            var merged = DomainResolver.MergeAddresses(fresh, old);

            Assert(merged.Length == 4, $"新 1 + 旧 3 = 4 个候选（实际 {merged.Length}）");
            Assert(merged[0].Equals(fresh[0]), "新结果排首位");
            Assert(old.All(a => merged.Contains(a)), "旧候选全部保留为兜底");
        }

        /// <summary>
        /// 去重与新优先级
        /// </summary>
        private static void TestMergeDedupAndOrder()
        {
            Console.WriteLine("候选合并：去重与顺序");
            var fresh = new[] { Ip("20.205.243.166"), Ip("140.82.121.3") };
            var old = new[] { Ip("140.82.121.3"), Ip("20.26.156.215"), Ip("20.26.156.215") };
            var merged = DomainResolver.MergeAddresses(fresh, old);

            Assert(merged.Length == 3, $"去重后 3 个（实际 {merged.Length}）");
            Assert(merged[0].Equals(fresh[0]) && merged[1].Equals(fresh[1]), "新结果保持原有顺序且优先");
            Assert(merged.Distinct().Count() == merged.Length, "结果内无重复");
        }

        /// <summary>
        /// 上限保护：不能因合并把候选池撑到无限大（探测风暴）
        /// </summary>
        private static void TestMergeRespectsLimit()
        {
            Console.WriteLine("候选合并：上限");
            var fresh = Enumerable.Range(1, 12).Select(i => Ip($"20.0.0.{i}")).ToArray();
            var old = Enumerable.Range(1, 12).Select(i => Ip($"140.82.0.{i}")).ToArray();
            var merged = DomainResolver.MergeAddresses(fresh, old);

            Assert(merged.Length == DomainResolver.MAX_MERGED_ADDRESS_COUNT,
                $"结果不超过上限 {DomainResolver.MAX_MERGED_ADDRESS_COUNT}（实际 {merged.Length}）");
            Assert(merged.Take(8).All(a => a.ToString().StartsWith("20.0.0.")), "上限内全部来自新结果");
        }

        private static void TestMergeEmptyOld()
        {
            Console.WriteLine("候选合并：无旧候选");
            var fresh = new[] { Ip("20.205.243.166"), Ip("140.82.121.3") };
            var merged = DomainResolver.MergeAddresses(fresh, Array.Empty<IPAddress>());
            Assert(merged.Length == 2 && merged[0].Equals(fresh[0]), "无旧候选时原样返回新结果");
        }

        /// <summary>
        /// 成功 IP 记忆：置顶且不打乱其余顺序
        /// </summary>
        private static void TestReorderSuccessIp()
        {
            Console.WriteLine("成功IP记忆：置顶");
            var addresses = new[] { Ip("140.82.121.3"), Ip("20.205.243.166"), Ip("20.26.156.215") };
            var success = Ip("20.205.243.166");
            var reordered = DomainResolver.ReorderWithFirst(addresses, success);

            Assert(reordered[0].Equals(success), "刚连接成功的 IP 被提到首位");
            Assert(reordered.Length == 3 && reordered.Distinct().Count() == 3, "其余候选不丢不重");
            Assert(addresses[0].Equals(Ip("140.82.121.3")), "原数组未被就地修改");

            var alreadyFirst = DomainResolver.ReorderWithFirst(addresses, addresses[0]);
            Assert(ReferenceEquals(alreadyFirst, addresses), "已在首位时原样返回（无谓的写操作）");
        }

        /// <summary>
        /// 成功 IP 不在候选里时不得引入新 IP
        /// </summary>
        private static void TestReorderUnknownIp()
        {
            Console.WriteLine("成功IP记忆：未知IP");
            var addresses = new[] { Ip("140.82.121.3"), Ip("20.26.156.215") };
            var reordered = DomainResolver.ReorderWithFirst(addresses, Ip("1.2.3.4"));
            Assert(ReferenceEquals(reordered, addresses), "未知 IP 不引入候选池");
        }
    }
}
