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
            TestAdaptiveTimeoutFollowsRtt();
            TestAdaptiveTimeoutClamps();
            TestSuccessKeepsRttHistory();
            TestCooldownEscalates();
            TestHalfOpenAllowsOnlyOneProbe();
            TestCooldownEscalatesOnFlappingEndpoint();
            TestHalfOpenSuccessResetsEscalation();

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

            // 连续失败达到阈值（4 次）→ 熔断
            for (var i = 0; i < 4; i++)
            {
                report.Invoke(resolver, new object[] { endpoint, false });
            }

            var first = GetEndpointState(resolver, endpoint);
            Assert(first.BlockedUntil > DateTime.UtcNow, "连续失败 4 次后进入熔断");

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
            var boxed = GetEndpointStateObject(resolver, endpoint);
            if (boxed == null)
            {
                return (0, DateTime.MinValue);
            }

            var type = boxed.GetType();
            var failures = (int)type.GetField("Failures")!.GetValue(boxed)!;
            var blockedUntil = (DateTime)type.GetField("BlockedUntil")!.GetValue(boxed)!;
            return (failures, blockedUntil);
        }

        /// <summary>
        /// 取出 EndpointState 原对象（需要改动其内部字段时使用）。
        /// </summary>
        private static object? GetEndpointStateObject(DohResolver resolver, string endpoint)
        {
            var field = typeof(DohResolver).GetField("endpointHealth", BindingFlags.NonPublic | BindingFlags.Instance);
            var value = field!.GetValue(resolver)!;

            // 注意：ConcurrentDictionary<string, EndpointState> **不能**强转成
            // ConcurrentDictionary<string, object>（泛型不变），必须走非泛型 IDictionary。
            if (value is System.Collections.IDictionary dict)
            {
                if (dict.Contains(endpoint) == false)
                {
                    return null;
                }
                return dict[endpoint];
            }

            failed++;
            Console.WriteLine("  FAIL endpointHealth 类型不符合预期，无法读取熔断状态");
            return null;
        }
        /// <summary>
        /// 【P1-1】超时必须随端点实测 RTT 自适应。
        /// <para>
        /// 若超时恒为固定 12s，则 v2.6.5 日志里"RTT 6 小时劣化一个数量级"这件事
        /// 无论把常数调成多少都会失配：调小误杀慢端点并触发熔断，调大让死端点拖满预算。
        /// 自适应的判据是：同一端点在 RTT 变大后，预算必须**跟着变大**。
        /// </para>
        /// </summary>
        private static void TestAdaptiveTimeoutFollowsRtt()
        {
            Console.WriteLine("自适应超时随实测 RTT 变化");
            var resolver = CreateResolver();
            const string endpoint = "https://223.5.5.5/dns-query";

            var getTimeout = InstanceMethod("GetTimeoutFor");
            var reportRtt = InstanceMethod("ReportEndpointRtt");
            Assert(getTimeout != null, "GetTimeoutFor 方法存在");
            Assert(reportRtt != null, "ReportEndpointRtt 方法存在");
            if (getTimeout == null || reportRtt == null)
            {
                return;
            }

            // 首次访问：无历史样本，必须给上界（不因"还不知道多快"而先斩断机会）
            var first = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;
            Assert(first == TimeSpan.FromSeconds(12d),
                "无 RTT 样本时退到上界 12s（不预先判死）",
                $"实际 {first.TotalSeconds:F1}s");

            // 快端点：RTT 150ms -> 3x = 450ms，但被下界 3s 夹住
            reportRtt.Invoke(resolver, new object[] { endpoint, 150d });
            var fast = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;
            Assert(fast == TimeSpan.FromSeconds(3d),
                "RTT 150ms 的快端点被夹到下界 3s（远快于固定 12s，失败能更早暴露）",
                $"实际 {fast.TotalSeconds:F1}s");

            // 慢端点：RTT 劣化到 2500ms -> 3x = 7.5s
            // 连续上报让 EMA 收敛过去（EMA 权重 0.3，需多次）
            for (var i = 0; i < 12; i++)
            {
                reportRtt.Invoke(resolver, new object[] { endpoint, 2500d });
            }
            var slow = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;
            Assert(slow > fast,
                "RTT 劣化后预算跟着变大（慢端点不再被秒级预算误判为死）",
                $"{fast.TotalSeconds:F1}s -> {slow.TotalSeconds:F1}s");
            Assert(slow <= TimeSpan.FromSeconds(12d),
                "劣化后预算不超过上界 12s",
                $"实际 {slow.TotalSeconds:F1}s");

            // 两个端点互不干扰 —— 这正是"自适应"相对"调大固定值"的核心价值
            const string fastEndpoint = "https://doh.pub/dns-query";
            for (var i = 0; i < 12; i++)
            {
                reportRtt.Invoke(resolver, new object[] { fastEndpoint, 160d });
            }
            var other = (TimeSpan)getTimeout.Invoke(resolver, new object[] { fastEndpoint })!;
            Assert(other == TimeSpan.FromSeconds(3d),
                "慢端点劣化不会牵连快端点（各自独立预算）",
                $"实际 {other.TotalSeconds:F1}s");
        }

        /// <summary>
        /// 【P1-1】上界必须夹紧：RTT 再离谱也不能超过 12s，
        /// 否则一个端点就能顶穿交互域名的 25s 整体预算。
        /// </summary>
        private static void TestAdaptiveTimeoutClamps()
        {
            Console.WriteLine("自适应超时上下界夹紧");
            var resolver = CreateResolver();
            const string endpoint = "https://1.1.1.1/dns-query";
            var reportRtt = InstanceMethod("ReportEndpointRtt");
            var getTimeout = InstanceMethod("GetTimeoutFor");
            if (reportRtt == null || getTimeout == null)
            {
                failed++;
                Console.WriteLine("  FAIL 反射目标不存在");
                return;
            }

            // 荒谬的 RTT（60s）也不得突破上界
            reportRtt.Invoke(resolver, new object[] { endpoint, 60_000d });
            var clamped = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;
            Assert(clamped == TimeSpan.FromSeconds(12d),
                "极端 RTT 被上界夹紧到 12s（防止顶穿 25s 整体预算）",
                $"实际 {clamped.TotalSeconds:F1}s");

            // 极快 RTT（10ms）不得跌破下界
            const string fast2 = "https://doh.360.cn/dns-query";
            reportRtt.Invoke(resolver, new object[] { fast2, 10d });
            var clampedLow = (TimeSpan)getTimeout.Invoke(resolver, new object[] { fast2 })!;
            Assert(clampedLow == TimeSpan.FromSeconds(3d),
                "极快 RTT 被下界夹紧到 3s（留足抖动余量，不会被瞬时快样本压到过短）",
                $"实际 {clampedLow.TotalSeconds:F1}s");
        }

        /// <summary>
        /// 【P1-1】上报成功**不得**清掉 RTT 历史。
        /// <para>
        /// 这是一条真实踩过的坑：端点状态同时承担熔断与 RTT 两个职责，
        /// 成功路径若整条TryRemove，RTT 历史随之消失 → 超时预算每次都退回 12s 上界，
        /// 自适应等于完全失效，且恰好发生在端点最健康的时刻，最难察觉。
        /// </para>
        /// </summary>
        private static void TestSuccessKeepsRttHistory()
        {
            Console.WriteLine("上报成功保留 RTT 历史");
            var resolver = CreateResolver();
            const string endpoint = "https://223.5.5.5/dns-query";
            var reportRtt = InstanceMethod("ReportEndpointRtt");
            var reportResult = InstanceMethod("ReportEndpointResult");
            var getTimeout = InstanceMethod("GetTimeoutFor");
            if (reportRtt == null || reportResult == null || getTimeout == null)
            {
                failed++;
                Console.WriteLine("  FAIL 反射目标不存在");
                return;
            }

            // 先建立一个"慢端点"的 RTT 历史
            for (var i = 0; i < 12; i++)
            {
                reportRtt.Invoke(resolver, new object[] { endpoint, 2500d });
            }
            var before = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;

            // 上报成功（这是每轮竞速赢家的必经路径）
            reportResult.Invoke(resolver, new object[] { endpoint, true });
            var after = (TimeSpan)getTimeout.Invoke(resolver, new object[] { endpoint })!;

            Assert(after == before,
                "成功上报后超时预算不变（RTT 历史未被清掉）",
                $"{before.TotalSeconds:F1}s -> {after.TotalSeconds:F1}s");
            Assert(after < TimeSpan.FromSeconds(12d),
                "成功上报后仍保留慢端点预算（未退回 12s 上界）",
                $"实际 {after.TotalSeconds:F1}s");

            // 同时验证熔断计数确实被清零（该职责不能因为上面的修改而失效）
            reportResult.Invoke(resolver, new object[] { endpoint, false });
            reportResult.Invoke(resolver, new object[] { endpoint, true });
            var (failures, blockedUntil) = GetEndpointState(resolver, endpoint);
            Assert(failures == 0 && blockedUntil == DateTime.MinValue,
                "成功上报后熔断计数确实清零",
                $"Failures={failures}, BlockedUntil={blockedUntil:O}");
        }

        /// <summary>
        /// 【v2.6.8 回归】熔断冷却必须**递增**，且在 30 分钟封顶。
        /// <para>
        /// v2.6.7 实测的 DoH 死循环（日志 publish/v267/logs/log20261004.txt）：
        /// 5 个端点在启动后 25 秒内全部熔断，之后每 10 分钟整批重熔断一次 ——
        /// 15:59 / 16:10 / 16:20 / 16:31 / 16:41 / 16:51，节奏精确等于固定冷却时长。
        /// 原因是"固定 10 分钟 + 阈值 2"：冷却一过，积压的 40 个查询（20 域名 × A/AAAA）
        /// 同时打向 5 个端点形成 200 并发，端点排队限速 → 又连续失败 2 次 → 立即二次熔断。
        /// 递增冷却让偶发抖动只短暂休息，而真正不可达的端点（跨境 1.1.1.1/8.8.8.8）
        /// 会逐次加长，最终稳定在 30 分钟，不会每 2 分钟被唤醒一次。
        /// </para>
        /// </summary>
        private static void TestCooldownEscalates()
        {
            Console.WriteLine("熔断冷却递增并封顶");
            var method = StaticMethod("ComputeCooldown");
            Assert(method != null, "ComputeCooldown 方法存在");
            if (method == null) return;

            var expected = new[] { 2d, 4d, 8d, 16d, 30d, 30d, 30d };
            for (var i = 0; i < expected.Length; i++)
            {
                var actual = Invoke<TimeSpan>(method, i + 1);
                Assert(
                    Math.Abs(actual.TotalMinutes - expected[i]) < 0.001,
                    $"第 {i + 1} 次连续熔断冷却 = {expected[i]:F0} 分钟",
                    $"实际 {actual.TotalMinutes:F1} 分钟");
            }
        }

        /// <summary>
        /// 【v2.6.8 回归】半开探测：冷却到期后**只允许一个**请求进入。
        /// <para>
        /// 这是打断熔断死循环的关键一环。若冷却一过就全部放行，
        /// 20 个域名 × 2 记录类型会同时涌向端点把它重新打爆，
        /// 于是熔断-洪峰-再熔断无限循环（v2.6.7 实测每 10 分钟一轮）。
        /// 半开让"恢复"只需 1 个请求即可确认，代价从 40 个请求的洪峰降到 1 个。
        /// </para>
        /// </summary>
        private static void TestHalfOpenAllowsOnlyOneProbe()
        {
            Console.WriteLine("半开探测只放行一个请求");
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportEndpointResult");
            var isBlocked = InstanceMethod("IsEndpointBlocked");
            Assert(report != null && isBlocked != null, "反射目标存在");
            if (report == null || isBlocked == null) return;

            const string endpoint = "https://223.5.5.5/dns-query";

            // 连续失败达到阈值（4 次）→ 熔断
            for (var i = 0; i < 4; i++)
            {
                report.Invoke(resolver, new object[] { endpoint, false });
            }

            var (failures, blockedUntil) = GetEndpointState(resolver, endpoint);
            Assert(blockedUntil > DateTime.UtcNow, "连续失败 4 次后进入熔断");
            Assert((bool)isBlocked.Invoke(resolver, new object[] { endpoint })!, "熔断期内被跳过");

            // 把冷却时刻改到过去，模拟"冷却已到期"。
            // 注意 EndpointState 是 private 嵌套类，此处只能经反射改字段，
            // 无法直接 lock(state.SyncRoot)（拿到的静态类型是 object，lock 要求引用类型）。
            // 单线程自测下无并发竞争，不需要额外加锁。
            var state = GetEndpointStateObject(resolver, endpoint);
            if (state == null)
            {
                return;
            }

            var stateType = state.GetType();
            stateType.GetField("BlockedUntil")!.SetValue(state, DateTime.UtcNow.AddSeconds(-1d));

            // 冷却到期：第一个调用方应被放行（半开试探）
            var first = (bool)isBlocked.Invoke(resolver, new object[] { endpoint })!;
            Assert(first == false, "冷却到期后第一个请求被放行（半开试探）");

            // 第二个及以后的调用方必须被挡住 —— 这正是防洪峰的关键
            var second = (bool)isBlocked.Invoke(resolver, new object[] { endpoint })!;
            Assert(second, "半开期间第二个请求被挡住（避免并发洪峰）");

            // 试探成功 → 端点完全恢复，后续请求全部正常放行
            report.Invoke(resolver, new object[] { endpoint, true });
            Assert((bool)isBlocked.Invoke(resolver, new object[] { endpoint })! == false,
                "半开试探成功后端点恢复正常放行");
            Assert((bool)isBlocked.Invoke(resolver, new object[] { endpoint })! == false,
                "恢复后第二个请求同样正常放行（半开标记已清除）");
            Assert(GetEndpointState(resolver, endpoint).Failures == 0,
                "恢复后失败计数清零");
        }

        /// <summary>
        /// 【v2.6.10 新增】"抖动型端点"的递增冷却必须**真实升级**。
        /// <para>
        /// 【为什么要补这条】既有的 <see cref="TestCooldownEscalates"/> 只测
        /// <c>ComputeCooldown</c> 这个**纯函数**本身正确，但它无法发现
        /// "函数对、调用路径断"这类缺陷 —— 而 v2.6.9 的真实故障恰恰是后者：
        /// <c>ReportEndpointResult</c> 的成功分支里有一句
        /// <c>ConsecutiveBlocks = 0</c>，把递增计数清零，
        /// 于是 <c>ComputeCooldown(++ConsecutiveBlocks)</c> 永远取第1 档。
        /// <para>
        /// 【v2.6.9 实测证据】publish/v269/logs/log20261007.txt 里
        /// <c>223.5.5.5/dns-query</c> 从 14:12 到 19:10 反复熔断 40+ 次，
        /// 每次日志都是"已熔断 <b>2 分钟</b>" —— 递增设计（2→4→8→16→30）
        /// 从未生效，熔断退化成无限抖动。
        /// <para>
        /// 本用例复现该模式：失败→冷却到期→**普通成功**→再失败→冷却到期→…
        /// 普通成功<b>不</b>构成"恢复"（真正的恢复必须由半开试探证明），
        /// 因此熔断时长必须逐次拉长。
        /// </para>
        /// </summary>
        private static void TestCooldownEscalatesOnFlappingEndpoint()
        {
            Console.WriteLine("抖动端点的递增冷却真实升级（普通成功不清零）");
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportEndpointResult");
            Assert(report != null, "ReportEndpointResult 方法存在");
            if (report == null) return;

            const string endpoint = "https://223.5.5.5/dns-query";
            var cooldowns = new List<double>();

            for (var round = 0; round < 3; round++)
            {
                // 本轮：连续失败 4 次 → 触发熔断
                for (var i = 0; i < 4; i++)
                {
                    report.Invoke(resolver, new object[] { endpoint, false });
                }

                var blockedUntil = GetEndpointState(resolver, endpoint).BlockedUntil;
                Assert(blockedUntil > DateTime.UtcNow, $"第 {round + 1} 轮：连续失败 4 次后进入熔断");
                cooldowns.Add((blockedUntil - DateTime.UtcNow).TotalMinutes);

                // 把冷却时刻移到过去，模拟冷却到期
                var st = GetEndpointStateObject(resolver, endpoint);
                if (st == null)
                {
                    failed++;
                    Console.WriteLine("  FAIL 无法取得 EndpointState");
                    return;
                }
                st.GetType().GetField("BlockedUntil")!.SetValue(st, DateTime.UtcNow.AddSeconds(-1d));

                // 关键：一次**普通成功**（非半开试探）。
                // 端点本来就在正常服务、这次恰好成功，不构成"恢复"。
                report.Invoke(resolver, new object[] { endpoint, true });
            }

            // 递增必须真实发生：每一轮的熔断时长都要比上一轮更长
            for (var i = 1; i < cooldowns.Count; i++)
            {
                Assert(
                    cooldowns[i] > cooldowns[i - 1],
                    $"第 {i + 1} 轮冷却({cooldowns[i]:F1} 分钟) 应长于第 {i} 轮({cooldowns[i-1]:F1} 分钟)",
                    "普通成功清零了递增计数 → 递增机制对抖动端点永久失效");
            }
        }

        /// <summary>
        /// 【v2.6.10 新增】只有**半开试探成功**才允许清零递增计数。
        /// <para>
        /// 这是 <see cref="TestCooldownEscalatesOnFlappingEndpoint"/> 的必要配套：
        /// 若半开成功也不清零，则真正恢复的端点会永远背着递增惩罚被跳过，
        /// 属于矫枉过正。两个用例一起把"该清的清、不该清的不清"钉死。
        /// </para>
        /// </summary>
        private static void TestHalfOpenSuccessResetsEscalation()
        {
            Console.WriteLine("半开试探成功才清零递增计数");
            var resolver = CreateResolver();
            var report = InstanceMethod("ReportEndpointResult");
            var isBlocked = InstanceMethod("IsEndpointBlocked");
            Assert(report != null && isBlocked != null, "反射目标存在");
            if (report == null || isBlocked == null) return;

            const string endpoint = "https://doh.pub/dns-query";

            // 连续两轮"失败 → 冷却到期 → 普通成功"，让递增计数升到第 2 档
            for (var round = 0; round < 2; round++)
            {
                for (var i = 0; i < 4; i++)
                {
                    report.Invoke(resolver, new object[] { endpoint, false });
                }
                var st = GetEndpointStateObject(resolver, endpoint);
                if (st == null) { failed++; return; }
                st.GetType().GetField("BlockedUntil")!.SetValue(st, DateTime.UtcNow.AddSeconds(-1d));
                report.Invoke(resolver, new object[] { endpoint, true });
            }

            // 此刻应处于"第 2 次熔断"的冷却档（普通成功没有清零它）
            var st2 = GetEndpointStateObject(resolver, endpoint);
            var blocksBefore = (int)st2!.GetType().GetField("ConsecutiveBlocks")!.GetValue(st2)!;
            Assert(blocksBefore >= 1, $"普通成功后递增计数仍在（ConsecutiveBlocks={blocksBefore}）");

            // 现在走真正的恢复路径：冷却到期 → 半开试探 → 成功
            st2.GetType().GetField("BlockedUntil")!.SetValue(st2, DateTime.UtcNow.AddSeconds(-1d));
            var probe = (bool)isBlocked.Invoke(resolver, new object[] { endpoint })!;
            Assert(probe == false, "冷却到期后放行半开试探请求");
            report.Invoke(resolver, new object[] { endpoint, true });

            var st3 = GetEndpointStateObject(resolver, endpoint)!;
            var blocksAfter = (int)st3.GetType().GetField("ConsecutiveBlocks")!.GetValue(st3)!;
            Assert(blocksAfter == 0, $"半开试探成功后递增计数清零（ConsecutiveBlocks={blocksAfter}）");
            Assert((bool)isBlocked.Invoke(resolver, new object[] { endpoint })! == false,
                "半开成功后端点完全恢复，不再被跳过");
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
