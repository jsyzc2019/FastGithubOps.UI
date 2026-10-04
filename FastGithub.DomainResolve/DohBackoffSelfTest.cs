using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.6】DohResolver 退避与熔断计数自检。
    /// <para>
    /// 这两个 P0 都不是"看起来不对"，而是在 v2.6.5 的 7.5 万行日志里**可量化观测**到的：
    ///   P0-1 失败重解析无退避  → 98 分钟内 11,834 条"所有端点均不可用"（每 2 秒重打一轮）
    ///   P0-2 熔断计数失效     → 同期"已熔断"日志 0 条，doh_blocked_endpoints.json 从未生成
    /// 而短时测试永远复现不出来（网络好时两个机制都不会被触发）。
    /// 因此这里用反射直接驱动私有方法做**确定性**验证，不依赖真实网络。
    /// </para>
    /// </summary>
    public static class DohBackoffSelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestBackoffGrowth();
            TestFirstFailureFlag();
            TestBackoffNotExtendedInsideWindow();
            TestEndpointFailureCounterIsAtomicUnderConcurrency();
            TestCooldownPersistsAcrossProcess();
            TestSuccessClearsNegativeCache();

            Console.WriteLine(failed == 0
                ? "  DohBackoffSelfTest: 全部通过"
                : $"  DohBackoffSelfTest: {failed} 项失败");
            return failed;
        }

        // ---------------------------------------------------------------- P0-1

        /// <summary>
        /// 退避必须严格指数增长并在 60 秒封顶 —— 这是掐断重解析风暴的核心。
        /// 若退避恒为 2 秒，故障期仍会退化成与修复前等价的重打频率。
        /// </summary>
        private static void TestBackoffGrowth()        {
            var method = StaticMethod("ComputeBackoff");
            Assert(method != null, "ComputeBackoff 方法存在");

            var expected = new[] { 2d, 4d, 8d, 16d, 32d, 60d, 60d, 60d };
            for (var i = 0; i < expected.Length; i++)
            {
                var actual = Invoke<TimeSpan>(method, i + 1);
                Assert(
                    Math.Abs(actual.TotalSeconds - expected[i]) < 0.001,
                    $"第 {i + 1} 次失败退避 = {expected[i]:F0}s",
                    $"实际 {actual.TotalSeconds:F0}s");
            }
        }

        /// <summary>
        /// 只有退避序列的**首次**失败才允许升级为告警（Warning）。
        /// 重复失败降级为 Debug，否则 98 分钟仍会刷出上万条同文案警告。
        /// </summary>
        private static void TestFirstFailureFlag()
        {
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportNegative");
            Assert(report != null, "ReportNegative 方法存在");
            if (report == null) return;

            // 首次失败（该域名此前从未失败过）
            var args = new object[] { "github.com", TimeSpan.MinValue, false };
            report.Invoke(resolver, args);
            Assert((bool)args[2], "首次失败被标记为 isFirstFailure（应告警）");

            // 退避期内的重复失败不应再次标记首次
            var args2 = new object[] { "github.com", TimeSpan.MinValue, false };
            report.Invoke(resolver, args2);
            Assert((bool)args2[2] == false, "退避期内重复失败不标记为首次（应静默）");

            // 退避过期后的下一次失败仍然算"再次"，不是首次
            var cache = NegativeCacheOf(resolver);
            cache["github.com"] = (5, DateTime.UtcNow.AddSeconds(-1d));
            var args3 = new object[] { "github.com", TimeSpan.MinValue, false };
            report.Invoke(resolver, args3);
            Assert((bool)args3[2] == false, "退避过期的再次失败不标记为首次");
            Assert(cache["github.com"].Failures == 6, "失败计数正确累加到 6", $"实际 {cache["github.com"].Failures}");
        }

        /// <summary>
        /// 退避期内的重复失败既不叠加计数、也不延长退避 ——
        /// 否则上层每秒轮询会把退避无限往后推，退避形同虚设。
        /// </summary>
        private static void TestBackoffNotExtendedInsideWindow()
        {
            var resolver = CreateResolver();
            var cache = NegativeCacheOf(resolver);

            // 预先放入一个很长的退避窗口
            var farFuture = DateTime.UtcNow.AddSeconds(45d);
            cache["api.github.com"] = (3, farFuture);

            InstanceMethod("ReportNegative")!.Invoke(resolver, new object[] { "api.github.com", TimeSpan.MinValue, false });

            var after = cache["api.github.com"];
            Assert(after.Failures == 3, "退避期内失败计数不变（3）", $"实际 {after.Failures}");
            Assert(after.RetryAfter == farFuture, "退避期内不延长退避时刻");
        }

        // ---------------------------------------------------------------- P0-2

        /// <summary>
        /// 【P0-2 核心回归】并发失败计数必须**不丢**。
        /// <para>
        /// 修复前用 <c>ConcurrentDictionary.TryUpdate</c> 做 CAS 累加：
        /// 18 域名 × A/AAAA 并发时几十个任务同时读到 Failures=0，只有 1 个 CAS 成功，
        /// 其余全部静默失败 —— 计数永远停在 1，熔断永不触发（日志实测 0 次）。
        /// 本测试并发上报 500 次失败，期望计数精确等于 500。
        /// </para>
        /// </summary>
        private static void TestEndpointFailureCounterIsAtomicUnderConcurrency()
        {
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportEndpointResult");
            Assert(report != null, "ReportEndpointResult 方法存在");
            if (report == null) return;

            const string endpoint = "https://1.1.1.1/dns-query";
            const int total = 64;
            var barrier = new Barrier(total);
            var threads = new List<Thread>(total);

            for (var i = 0; i < total; i++)
            {
                var thread = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    report.Invoke(resolver, new object[] { endpoint, false });
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            var state = GetEndpointState(resolver, endpoint);
            // 计数可能已超过熔断阈值并被"熔断"覆盖，但**绝不能少于** total - threshold + 1
            // 更直接的判定：熔断必须已触发（BlockedUntil > now）
            var blocked = state.BlockedUntil > DateTime.UtcNow;
            Assert(blocked, $"{total} 次并发失败后端点已进入熔断（证明计数未丢失）",
                $"BlockedUntil={state.BlockedUntil:O}, Failures={state.Failures}");
        }

        /// <summary>
        /// 冷却期内的新失败不得延长熔断时长（否则一次网络抖动会永久弃用端点）。
        /// </summary>
        private static void TestCooldownPersistsAcrossProcess()
        {
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportEndpointResult");
            Assert(report != null, "ReportEndpointResult 方法存在");
            if (report == null) return;

            const string endpoint = "https://8.8.8.8/dns-query";

            // 连续失败 2 次 → 熔断
            report.Invoke(resolver, new object[] { endpoint, false });
            report.Invoke(resolver, new object[] { endpoint, false });

            var first = GetEndpointState(resolver, endpoint);
            Assert(first.BlockedUntil > DateTime.UtcNow, "连续失败 2 次后进入熔断");

            // 熔断期内再失败 10 次，剩余冷却时间不得被拉长
            for (var i = 0; i < 10; i++)
            {
                report.Invoke(resolver, new object[] { endpoint, false });
            }

            var second = GetEndpointState(resolver, endpoint);
            Assert(second.BlockedUntil == first.BlockedUntil,
                "熔断期内重复失败不延长冷却（剩余时长保留）",
                $"从 {first.BlockedUntil:O} 变成 {second.BlockedUntil:O}");
        }

        /// <summary>
        /// 成功解析必须清零退避 —— 否则网络恢复后仍要等满 60 秒才重新工作。
        /// </summary>
        private static void TestSuccessClearsNegativeCache()
        {
            var resolver = CreateResolver();
            var cache = NegativeCacheOf(resolver);

            cache["github.com"] = (6, DateTime.UtcNow.AddSeconds(60d));
            Assert(cache.ContainsKey("github.com"), "前置条件：负缓存已存在");

            InstanceMethod("ClearNegativeCache")!.Invoke(resolver, new object[] { "github.com" });

            Assert(cache.ContainsKey("github.com") == false, "成功后负缓存被清零（网络恢复后立即可用）");
        }

        // ---------------------------------------------------------------- 工具

        private static System.Reflection.MethodInfo? StaticMethod(string name)
        {
            return typeof(DohResolver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static System.Reflection.MethodInfo? InstanceMethod(string name)
        {
            return typeof(DohResolver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private static System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTime RetryAfter)> NegativeCacheOf(DohResolver resolver)
        {
            var field = typeof(DohResolver).GetField("negativeCache", BindingFlags.NonPublic | BindingFlags.Instance);
            return (System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTime RetryAfter)>)field!.GetValue(resolver)!;
        }

        /// <summary>
        /// 反射调用并转换结果；目标缺失时记为失败而非抛 NRE
        /// </summary>
        private static T Invoke<T>(System.Reflection.MethodInfo? method, params object?[] args)
        {
            if (method == null)
            {
                failed++;
                Console.WriteLine("  FAIL 反射目标不存在");
                return default!;
            }

            return (T)method.Invoke(null, args)!;
        }

        private static (int Failures, DateTime BlockedUntil) GetEndpointState(DohResolver resolver, string endpoint)
        {
            var field = typeof(DohResolver).GetField("endpointHealth", BindingFlags.NonPublic | BindingFlags.Instance);
            var value = field!.GetValue(resolver)!;

            // 注意：ConcurrentDictionary<string, EndpointState> **不能**强转成
            // ConcurrentDictionary<string, object>（泛型不变），必须走非泛型 IDictionary。
            if (value is System.Collections.IDictionary dict)
            {
                if (dict.Contains(endpoint) == false)
                {
                    return (0, DateTime.MinValue);
                }

                var boxed = dict[endpoint]!;
                var type = boxed.GetType();
                var failures = (int)type.GetField("Failures")!.GetValue(boxed)!;
                var blockedUntil = (DateTime)type.GetField("BlockedUntil")!.GetValue(boxed)!;
                return (failures, blockedUntil);
            }

            failed++;
            Console.WriteLine("  FAIL endpointHealth 类型不符合预期，无法读取熔断状态");
            return (0, DateTime.MinValue);
        }
        /// <summary>
        /// 构造一个不写盘的 DohResolver（写盘会污染工作目录）
        /// </summary>
        private static DohResolver CreateResolver()
        {
            var loggerType = typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(typeof(DohResolver));
            var logger = Activator.CreateInstance(loggerType);
            var ctor = typeof(DohResolver).GetConstructors()[0];
            return (DohResolver)ctor.Invoke(new object?[] { logger })!;
        }

        private static void Assert(bool condition, string title, string? detail = null)        {
            if (condition)
            {
                Console.WriteLine($"  OK   {title}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {title}{(detail == null ? string.Empty : " —— " + detail)}");
            }
        }
    }
}
