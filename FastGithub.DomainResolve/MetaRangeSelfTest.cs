using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.12 B1】MetaIpRangesService 自检。
    /// <para>
    /// meta 段采样是"往候选池里塞官方段内 IP"的增益机制——它一旦出错
    /// （段解析错位、采样越界、把环回/重复 IP 塞进池里），错误候选会直接进入
    /// 握手竞速与候选排序，表现为" inexplicable 的慢"，几乎无法从日志定位。
    /// 因此这里对 CIDR 解析、段归属判定、采样上限/去重/确定性做穷举式验证。
    /// </para>
    /// </summary>
    public static class MetaRangeSelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestCidrParseAndContains();
            TestCidrRejectsInvalid();
            TestRangeTypeForHost();
            TestSamplingRespectsLimitAndDedup();
            TestSamplingExcludesExisting();
            TestSamplingDeterministicPerSeed();
            TestParseRangesFromMetaJson();

            Console.WriteLine(failed == 0
                ? "MetaRangeSelfTest: 全部通过"
                : $"MetaRangeSelfTest: 失败 {failed} 项");
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

        private static List<MetaIpRangesService.CidrSegment> Segments(params string[] cidrs)
        {
            return cidrs
                .Select(c => MetaIpRangesService.CidrSegment.TryParse(c, out var s) ? s : throw new InvalidOperationException("测试段应可解析: " + c))
                .ToList();
        }

        /// <summary>
        /// CIDR 解析与归属判定（用真实 GitHub 官方段）
        /// </summary>
        private static void TestCidrParseAndContains()
        {
            Console.WriteLine("CIDR 解析与段归属");
            Assert(MetaIpRangesService.CidrSegment.TryParse("140.82.112.0/20", out var s1), "140.82.112.0/20 可解析");
            Assert(s1.Contains(IPAddress.Parse("140.82.121.3")), "140.82.121.3 落在 140.82.112.0/20 内");
            Assert(s1.Contains(IPAddress.Parse("140.82.127.254")), "140.82.127.254 落在段末尾");
            Assert(s1.Contains(IPAddress.Parse("140.82.128.1")) == false, "140.82.128.1 不在段内");
            Assert(s1.Contains(IPAddress.Parse("140.82.111.255")) == false, "140.82.111.255 不在段前界");

            Assert(MetaIpRangesService.CidrSegment.TryParse("185.199.108.0/22", out var s2), "185.199.108.0/22 可解析");
            Assert(s2.Contains(IPAddress.Parse("185.199.111.133")), "185.199.111.133（pages CDN）落在 185.199.108.0/22 内");
        }

        /// <summary>
        /// 非法输入必须被拒绝（污染源里混入的垃圾 CIDR 不能产生段）
        /// </summary>
        private static void TestCidrRejectsInvalid()
        {
            Console.WriteLine("CIDR 非法输入拒绝");
            Assert(MetaIpRangesService.CidrSegment.TryParse("not-a-cidr", out _) == false, "非 CIDR 字符串被拒绝");
            Assert(MetaIpRangesService.CidrSegment.TryParse("1.2.3.4", out _) == false, "缺前缀被拒绝");
            Assert(MetaIpRangesService.CidrSegment.TryParse("1.2.3.4/33", out _) == false, "前缀 33 被拒绝");
            Assert(MetaIpRangesService.CidrSegment.TryParse("1.2.3.4/7", out _) == false, "前缀小于 8 被拒绝（过宽段无意义）");
            Assert(MetaIpRangesService.CidrSegment.TryParse("::1/64", out _) == false, "IPv6 被拒绝（本项目仅 IPv4）");
        }

        /// <summary>
        /// 域名 → 段类型映射（api 用 api 段，github.com 系用 web 段，其余不适用）
        /// </summary>
        private static void TestRangeTypeForHost()
        {
            Console.WriteLine("域名段类型判定");
            Assert(MetaIpRangesService.GetRangeType("api.github.com") == "api", "api.github.com → api 段");
            Assert(MetaIpRangesService.GetRangeType("github.com") == "web", "github.com → web 段");
            Assert(MetaIpRangesService.GetRangeType("codeload.github.com") == "web", "codeload.github.com → web 段");
            Assert(MetaIpRangesService.GetRangeType("collector.github.com") == "web", "collector.github.com → web 段");
            Assert(MetaIpRangesService.GetRangeType("raw.githubusercontent.com") == null, "raw.githubusercontent.com 不适用（Fastly CDN，meta 不覆盖）");
            Assert(MetaIpRangesService.GetRangeType("camo.githubusercontent.com") == null, "camo.githubusercontent.com 不适用");
            Assert(MetaIpRangesService.GetRangeType("evil.example.com") == null, "无关域名不适用");
        }

        /// <summary>
        /// 采样必须尊重上限、去重
        /// </summary>
        private static void TestSamplingRespectsLimitAndDedup()
        {
            Console.WriteLine("采样上限与去重");
            var segments = Segments("140.82.112.0/20", "192.30.252.0/22", "185.199.108.0/22");
            var sampled = MetaIpRangesService.SampleFromSegments(segments, new HashSet<IPAddress>(), 4, seed: 42);
            Assert(sampled.Length == 4, $"请求 4 个应得 4 个（实际 {sampled.Length}）");
            Assert(sampled.Select(i => i.ToString()).Distinct().Count() == 4, "采样结果无重复");
            Assert(sampled.All(ip => segments.Any(s => s.Contains(ip))), "全部采样落在给定段内");
        }

        /// <summary>
        /// 采样必须避开已有候选（否则白塞一个重复 IP，浪费一个候选位）
        /// </summary>
        private static void TestSamplingExcludesExisting()
        {
            Console.WriteLine("采样避开已有候选");
            var segments = Segments("140.82.112.0/20");
            var existing = new HashSet<IPAddress> { IPAddress.Parse("140.82.112.1") };
            var sampled = MetaIpRangesService.SampleFromSegments(segments, existing, 3, seed: 7);
            Assert(sampled.All(ip => existing.Contains(ip) == false), "采样结果不含已有候选");
            Assert(sampled.All(ip => segments[0].Contains(ip)), "采样仍在段内");
        }

        /// <summary>
        /// 同 seed 确定性、不同 seed 有差异（保证跨轮次覆盖不同候选）
        /// </summary>
        private static void TestSamplingDeterministicPerSeed()
        {
            Console.WriteLine("采样确定性与多样性");
            var segments = Segments("140.82.112.0/20", "192.30.252.0/22", "140.82.96.0/20", "20.205.243.0/24");
            var a1 = MetaIpRangesService.SampleFromSegments(segments, new HashSet<IPAddress>(), 5, seed: 100);
            var a2 = MetaIpRangesService.SampleFromSegments(segments, new HashSet<IPAddress>(), 5, seed: 100);
            var b = MetaIpRangesService.SampleFromSegments(segments, new HashSet<IPAddress>(), 5, seed: 98765);

            Assert(a1.SequenceEqual(a2), "同 seed 两次采样完全一致（确定性）");
            Assert(a1.SequenceEqual(b) == false, "不同 seed 采样有差异（跨轮次多样性）");
        }

        /// <summary>
        /// meta JSON → 段字典（用精简的真实结构样例）
        /// </summary>
        private static void TestParseRangesFromMetaJson()
        {
            Console.WriteLine("meta JSON 解析");
            var json = @"{
                ""web"": [""140.82.112.0/20"", ""192.30.252.0/22"", ""not-valid"", ""2001:db8::/32""],
                ""api"": [""140.82.112.0/20""],
                ""git"": [""140.82.112.0/20"", ""192.30.252.0/22""],
                ""actions"": [""4.148.0.0/16""]
            }";
            var ranges = MetaIpRangesService.ParseRanges(json);
            Assert(ranges.Count == 3, $"只取 web/api/git 三类（实际 {ranges.Count}）");
            Assert(ranges["web"].Count == 2, $"web 段过滤非法/IPv6 后剩 2（实际 {ranges["web"].Count}）");
            Assert(ranges["api"].Count == 1, "api 段 1 个");
            Assert(ranges.ContainsKey("actions") == false, "actions 段不采集（与加速无关）");

            var broken = MetaIpRangesService.ParseRanges("{ broken json");
            Assert(broken.Count == 0, "畸形 JSON 返回空（不抛异常）");
        }
    }
}
