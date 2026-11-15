using System;

namespace FastGithub.Http
{
    /// <summary>
    /// 【v2.6.14 P1】遥测请求超时策略自检。
    /// <para>
    /// 这条策略的价值全在"边界判断"上：漏判等于没生效（504 里 66% 还是遥测），
    /// 误判则是**把正常请求**砍到 8 秒（例如把 api.github.com 的普通 API 请求当成 beacon），
    /// 会让用户在正常网络下莫名其妙地超时。所以两条边界都要钉死。
    /// </para>
    /// </summary>
    public static class TelemetryPolicySelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestCollectorHostIsTelemetry();
            TestBrowserTelemetryPath();
            TestNormalApiRequestUnaffected();
            TestLookalikeHostNotTelemetry();
            TestLimitTimeout();
            TestLimitTimeoutKeepsSmallerConfigured();

            Console.WriteLine(failed == 0
                ? "TelemetryPolicySelfTest: 全部通过"
                : $"TelemetryPolicySelfTest: 失败 {failed} 项");
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

        private static void TestCollectorHostIsTelemetry()
        {
            Console.WriteLine("collector 域名识别");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://collector.github.com/github/collect")),
                "collector.github.com/github/collect 判定为遥测");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://collector.githubapp.com/docs/collect")),
                "collector.githubapp.com 判定为遥测");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://COLLECTOR.GITHUB.COM/x")),
                "域名大小写不敏感");
        }

        private static void TestBrowserTelemetryPath()
        {
            Console.WriteLine("_private/browser 路径识别");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://api.github.com/_private/browser/rum")),
                "api.github.com/_private/browser/rum 判定为遥测");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://api.github.com/_private/browser/stats?x=1")),
                "带查询串的 _private/browser/stats 判定为遥测");
        }

        private static void TestNormalApiRequestUnaffected()
        {
            Console.WriteLine("正常请求不受影响（防误杀）");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://api.github.com/repos/a/b")) == false,
                "普通 API 请求不判为遥测");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://github.com/notifications/indicator")) == false,
                "轮询端点不判为遥测（仍需正常预算）");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://api.github.com/_private/repos/x")) == false,
                "_private 下非 browser 路径不判为遥测");
        }

        private static void TestLookalikeHostNotTelemetry()
        {
            Console.WriteLine("相似域名不误判");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://collector.github.com.evil.com/github/collect")) == false,
                "collector.github.com.evil.com 不判为遥测");
            Assert(TelemetryPolicy.IsTelemetryBeacon(new Uri("https://notcollector.githubapp.com/x")) == false,
                "notcollector.githubapp.com 不判为遥测");
        }

        private static void TestLimitTimeout()
        {
            Console.WriteLine("预算收紧");
            var capped = TelemetryPolicy.LimitTimeout(new Uri("https://collector.github.com/github/collect"), TimeSpan.FromSeconds(30d));
            Assert(capped == TelemetryPolicy.TelemetryTimeout, $"30s 预算收紧到 8s（实际 {capped}）");

            var fromNull = TelemetryPolicy.LimitTimeout(new Uri("https://api.github.com/_private/browser/rum"), null);
            Assert(fromNull == TelemetryPolicy.TelemetryTimeout, $"未配置预算时给出 8s（实际 {fromNull}）");

            var normal = TelemetryPolicy.LimitTimeout(new Uri("https://api.github.com/repos/a/b"), TimeSpan.FromSeconds(25d));
            Assert(normal == TimeSpan.FromSeconds(25d), $"普通请求保留域名配置 25s（实际 {normal}）");
        }

        private static void TestLimitTimeoutKeepsSmallerConfigured()
        {
            Console.WriteLine("不放松已有更小预算");
            var kept = TelemetryPolicy.LimitTimeout(new Uri("https://collector.github.com/github/collect"), TimeSpan.FromSeconds(5d));
            Assert(kept == TimeSpan.FromSeconds(5d), $"域名已配 5s 时保留 5s（实际 {kept}）");
        }
    }
}
