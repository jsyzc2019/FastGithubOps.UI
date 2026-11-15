using FastGithub.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 【v2.6.14 P0】候选IP 持久化自检。
    /// <para>
    /// 这条链路的价值和风险都很极端：
    /// 价值 —— 重启后候选池非空，直接消掉"启动后 15 分钟"的失败窗口（占历史 502/504 的 77%）；
    /// 风险 —— 文件格式一旦与读取端不一致（或旧版本遗留的半截文件），
    /// 重启时会把候选池静默清空，**比不做持久化更糟**，而且现象是"偶发打不开"，极难定位。
    /// 所以三条性质必须钉死：新格式往返无损、旧格式（只有 host/port）可读、坏文件不抛异常。
    /// </para>
    /// </summary>
    public static class PersistenceSelfTest
    {
        private static int failed;

        public static int Run()
        {
            var file = Path.Combine(
                Path.GetTempPath(),
                "fastgithub-persist-selftest-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                TestRoundTrip(file);
                TestLegacyFormat(file);
                TestCorruptedFile(file);
                TestInvalidAddressFiltered(file);
                TestUnknownDomainFiltered(file);
            }
            finally
            {
                TryDelete(file);
                TryDelete(file + ".tmp");
            }

            Console.WriteLine(failed == 0
                ? "PersistenceSelfTest: 全部通过"
                : $"PersistenceSelfTest: 失败 {failed} 项");
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

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception)
            {
            }
        }

        private static PersistenceService CreateService(string file)
        {
            var options = new FastGithubOptions();
            options.DomainConfigs["github.com"] = new DomainConfig();
            options.DomainConfigs["api.github.com"] = new DomainConfig();
            var config = new FastGithubConfig(new FakeOptionsMonitor(options));
            return new PersistenceService(config, NullLogger<PersistenceService>.Instance, file);
        }

        /// <summary>
        /// 新格式往返：域名、端口、候选IP、探测耗时都必须原样回来
        /// </summary>
        private static void TestRoundTrip(string file)
        {
            Console.WriteLine("候选IP 往返");
            TryDelete(file);

            var service = CreateService(file);
            var endPoint = new DnsEndPoint("github.com", 443);
            var items = new[]
            {
                new PersistedEndPoint(endPoint, new[]
                {
                    new PersistedAddress(IPAddress.Parse("140.82.121.4"), TimeSpan.FromMilliseconds(123.4)),
                    new PersistedAddress(IPAddress.Parse("140.82.121.5"), TimeSpan.FromMilliseconds(456)),
                }),
            };

            service.WriteEndPointsAsync(items, CancellationToken.None).GetAwaiter().GetResult();
            var loaded = service.ReadEndPoints();

            Assert(loaded.Count == 1, $"读回 1 个域名（实际 {loaded.Count}）");
            if (loaded.Count == 1)
            {
                Assert(loaded[0].EndPoint.Host == "github.com" && loaded[0].EndPoint.Port == 443, "域名与端口保持");
                Assert(loaded[0].Addresses.Count == 2, $"候选IP 数量保持（实际 {loaded[0].Addresses.Count}）");
                Assert(loaded[0].Addresses.Count == 2 &&
                       loaded[0].Addresses[0].Address.Equals(IPAddress.Parse("140.82.121.4")) &&
                       Math.Abs(loaded[0].Addresses[0].Elapsed.TotalMilliseconds - 123.4) < 0.2d,
                    "首个候选IP 与其探测耗时保持");
            }
        }

        /// <summary>
        /// 旧格式（只有 host/port）必须可读：升级安装后第一次启动读到的就是它
        /// </summary>
        private static void TestLegacyFormat(string file)
        {
            Console.WriteLine("旧格式兼容");
            File.WriteAllText(file, "[{\"host\":\"github.com\",\"port\":443},{\"host\":\"api.github.com\",\"port\":443}]");

            var service = CreateService(file);
            var loaded = service.ReadEndPoints();

            Assert(loaded.Count == 2, $"旧格式读回 2 个域名（实际 {loaded.Count}）");
            Assert(loaded.Count == 2 && loaded[0].Addresses.Count == 0, "旧格式无候选IP（等价旧行为，不抛异常）");
        }

        /// <summary>
        /// 半截/畸形文件：只能返回空列表，绝不能把进程带崩（ATOMIC 写入就是为了少产生这种文件）
        /// </summary>
        private static void TestCorruptedFile(string file)
        {
            Console.WriteLine("坏文件容错");
            File.WriteAllText(file, "[{\"host\":\"github");

            var service = CreateService(file);
            var loaded = service.ReadEndPoints();
            Assert(loaded.Count == 0, "畸形 JSON 返回空列表");
        }

        /// <summary>
        /// 被污染的候选（环回/内网）不得从磁盘复活
        /// </summary>
        private static void TestInvalidAddressFiltered(string file)
        {
            Console.WriteLine("无效IP 过滤");
            File.WriteAllText(
                file,
                "[{\"host\":\"github.com\",\"port\":443,\"addresses\":[" +
                "{\"address\":\"127.0.0.1\",\"elapsedMilliseconds\":10}," +
                "{\"address\":\"192.168.1.1\",\"elapsedMilliseconds\":10}," +
                "{\"address\":\"140.82.121.4\",\"elapsedMilliseconds\":10}]}]");

            var service = CreateService(file);
            var loaded = service.ReadEndPoints();

            Assert(loaded.Count == 1, "域名条目保留");
            Assert(loaded.Count == 1 && loaded[0].Addresses.Count == 1, $"仅保留公网IP（实际 {loaded[0].Addresses.Count} 个）");
        }

        /// <summary>
        /// 不在配置域名表里的 host 必须丢弃（防止手动编辑/投毒把流量导向任意域名）
        /// </summary>
        private static void TestUnknownDomainFiltered(string file)
        {
            Console.WriteLine("未知域名过滤");
            File.WriteAllText(
                file,
                "[{\"host\":\"example.com\",\"port\":443,\"addresses\":[{\"address\":\"93.184.216.34\",\"elapsedMilliseconds\":10}]}," +
                "{\"host\":\"github.com\",\"port\":443,\"addresses\":[{\"address\":\"140.82.121.4\",\"elapsedMilliseconds\":10}]}]");

            var service = CreateService(file);
            var loaded = service.ReadEndPoints();

            Assert(loaded.Count == 1 && loaded[0].EndPoint.Host == "github.com", "只保留配置内的域名");
        }

        /// <summary>
        /// 自检用的 IOptionsMonitor 桩：只需要 CurrentValue 与 OnChange
        /// </summary>
        private sealed class FakeOptionsMonitor : IOptionsMonitor<FastGithubOptions>
        {
            private readonly FastGithubOptions value;

            public FakeOptionsMonitor(FastGithubOptions value)
            {
                this.value = value;
            }

            public FastGithubOptions CurrentValue => this.value;

            public FastGithubOptions Get(string? name) => this.value;

            public IDisposable? OnChange(Action<FastGithubOptions, string?> listener) => null;
        }
    }
}
