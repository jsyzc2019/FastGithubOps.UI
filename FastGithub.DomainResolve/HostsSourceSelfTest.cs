using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.11】HostsService 源格式解析自检。
    /// <para>
    /// 借鉴上游 v2.3.2 引入 JSON hosts 源后，新增了两条解析路径与一次格式自动识别。
    /// 源内容会被**直接当作域名-&gt;IP 映射**使用，解析层的任何回归
    /// （JSON 解析失败、无效 IP 未过滤、文本格式被误判）都会直接把请求导向错误目标。
    /// 这里用反射驱动私有 <c>Parse</c> 做确定性验证，不依赖真实网络。
    /// </para>
    /// </summary>
    public static class HostsSourceSelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestTextFormatStillWorks();
            TestJsonFormatParses();
            TestJsonFiltersInvalidIp();
            TestJsonMalformedReturnsEmpty();
            TestJsonSingleItemWithExtraFields();
            TestTextInvalidIpFiltered();

            Console.WriteLine(failed == 0
                ? "HostsSourceSelfTest: 全部通过"
                : $"HostsSourceSelfTest: 失败 {failed} 项");
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

        /// <summary>
        /// 反射调用 HostsService.Parse(string, out int)
        /// </summary>
        private static Dictionary<string, IReadOnlyList<IPAddress>> InvokeParse(string content, out int invalidCount)
        {
            var method = typeof(HostsService).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new InvalidOperationException("找不到 HostsService.Parse（签名被改动后未同步自检）");
            }

            var args = new object[] { content, 0 };
            var result = (Dictionary<string, IReadOnlyList<IPAddress>>)method.Invoke(null, args);
            invalidCount = (int)args[1];
            return result;
        }

        /// <summary>
        /// 原文本格式必须继续工作（回归保护：引入 JSON 分支不能破坏老格式）
        /// </summary>
        private static void TestTextFormatStillWorks()
        {
            Console.WriteLine("hosts 文本格式解析");
            var map = InvokeParse("# comment\n140.82.121.4 github.com\n185.199.108.133 raw.githubusercontent.com\n", out var invalid);
            Assert(map.Count == 2, $"文本格式解析出 2 个域名（实际 {map.Count}）");
            Assert(invalid == 0, $"文本格式无无效IP（实际 {invalid}）");
            Assert(map.TryGetValue("github.com", out var ips) && ips.Count == 1 && ips[0].Equals(IPAddress.Parse("140.82.121.4")),
                "文本格式域名->IP 映射正确");
        }

        /// <summary>
        /// JSON 数组格式（上游新源格式）必须可解析
        /// </summary>
        private static void TestJsonFormatParses()
        {
            Console.WriteLine("hosts JSON 格式解析");
            var json = "[[\"140.82.121.4\",\"github.com\"],[\"185.199.108.133\",\"raw.githubusercontent.com\"],[\"185.199.109.133\",\"raw.githubusercontent.com\"]]";
            var map = InvokeParse(json, out var invalid);
            Assert(map.Count == 2, $"JSON 解析出 2 个域名（实际 {map.Count}）");
            Assert(invalid == 0, $"JSON 无无效IP（实际 {invalid}）");
            Assert(map.TryGetValue("raw.githubusercontent.com", out var ips) && ips.Count == 2,
                "JSON 同域名多 IP 正确聚合");
        }

        /// <summary>
        /// JSON 源同样必须过滤环回/内网等无效 IP（防投毒与文本格式同权）
        /// </summary>
        private static void TestJsonFiltersInvalidIp()
        {
            Console.WriteLine("JSON 无效IP 过滤");
            var json = "[[\"127.0.0.1\",\"github.com\"],[\"192.168.1.1\",\"api.github.com\"],[\"140.82.121.4\",\"github.com\"]]";
            var map = InvokeParse(json, out var invalid);
            Assert(invalid == 2, $"环回+内网 IP 被过滤（实际过滤 {invalid}）");
            Assert(map.TryGetValue("github.com", out var ips) && ips.Count == 1 && ips[0].Equals(IPAddress.Parse("140.82.121.4")),
                "过滤后仅剩有效 IP");
            Assert(map.ContainsKey("api.github.com") == false, "全无效条目不产生映射");
        }

        /// <summary>
        /// 畸形 JSON 返回空映射而不是抛异常（调用方保留旧映射）
        /// </summary>
        private static void TestJsonMalformedReturnsEmpty()
        {
            Console.WriteLine("JSON 畸形内容容错");
            var map = InvokeParse("[[\"broken\",", out _);
            Assert(map.Count == 0, "畸形 JSON 返回空映射（不抛异常）");
        }

        /// <summary>
        /// 条目字段不足时跳过而不抛异常
        /// </summary>
        private static void TestJsonSingleItemWithExtraFields()
        {
            Console.WriteLine("JSON 条目容错");
            var json = "[[\"140.82.121.4\",\"github.com\"],[\"1.2.3.4\"],[null],[\"140.82.121.4\",\"github.com\"]]";
            var map = InvokeParse(json, out _);
            Assert(map.Count == 1 && map["github.com"].Count == 1,
                "字段不足/重复条目被跳过或去重（实际 github.com -> " +
                (map.TryGetValue("github.com", out var ips) ? ips.Count : 0) + " 个IP）");
        }

        /// <summary>
        /// 文本格式的无效 IP 过滤保持生效（原有防线不可因重构丢失）
        /// </summary>
        private static void TestTextInvalidIpFiltered()
        {
            Console.WriteLine("文本无效IP 过滤");
            var map = InvokeParse("127.0.0.1 github.com\n10.0.0.1 api.github.com\n140.82.121.4 codeload.github.com\n", out var invalid);
            Assert(invalid == 2, $"环回+内网 IP 被过滤（实际过滤 {invalid}）");
            Assert(map.Count == 1 && map.ContainsKey("codeload.github.com"), "仅有效条目入选");
        }
    }
}
