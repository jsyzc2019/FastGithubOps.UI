using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 持久化的单个候选IP（含上次探测到的握手耗时）
    /// </summary>
    /// <param name="Address">IP</param>
    /// <param name="Elapsed">上次探测的握手指令耗时；<see cref="TimeSpan.MaxValue"/> 表示不可达</param>
    public sealed record PersistedAddress(IPAddress Address, TimeSpan Elapsed);

    /// <summary>
    /// 持久化的域名状态
    /// </summary>
    /// <param name="EndPoint">域名节点</param>
    /// <param name="Addresses">候选IP（按健康度/时延排好序）</param>
    public sealed record PersistedEndPoint(DnsEndPoint EndPoint, IReadOnlyList<PersistedAddress> Addresses);

    /// <summary>
    /// 域名持久化
    /// <para>
    /// 【v2.6.14 P0】从"只存 host:port"升级为"host:port + 候选IP + 探测耗时"。
    /// 旧实现的致命缺口：文件里没有 IP，进程重启瞬间所有域名的候选池都是空的，
    /// 必须重新走一遍 hosts 源 -> DoH -> 明文 DNS 才能拿到第一个 IP。
    /// 实测 11-15 的日志里 502/504 的 77% 落在每次启动后的 15 分钟内，正是这段空窗。
    /// </para>
    /// <para>
    /// 兼容性：旧文件只有 <c>host</c>/<c>port</c> 两个字段，反序列化后 <c>addresses</c>
    /// 为 null，等价于旧行为（启动时无候选IP）；新文件多两个字段，旧版本读取会忽略它们。
    /// 因此降级安装也不会因格式变化而崩溃。
    /// </para>
    /// </summary>
    sealed partial class PersistenceService
    {
        private static readonly string defaultDataFile = "dnsendpoints.json";
        private static readonly SemaphoreSlim dataLocker = new(1, 1);

        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<PersistenceService> logger;
        private readonly string dataFile;
        private readonly string tempFile;

        /// <summary>
        /// 单个候选IP的落盘形态（IP 用字符串，避免 IPAddress 自带的 JSON 转换器依赖）
        /// </summary>
        private record EndPointAddressItem(string? Address, double ElapsedMilliseconds);

        private record EndPointItem(
            string? Host,
            int Port,
            EndPointAddressItem[]? Addresses,
            long SavedAtUtc);

        [JsonSerializable(typeof(EndPointItem[]))]
        [JsonSourceGenerationOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
        private partial class EndPointItemsContext : JsonSerializerContext
        {
        }


        /// <summary>
        /// 域名持久化
        /// </summary> 
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public PersistenceService(
            FastGithubConfig fastGithubConfig,
            ILogger<PersistenceService> logger)
            : this(fastGithubConfig, logger, defaultDataFile)
        {
        }

        /// <summary>
        /// 域名持久化（可指定文件名，仅供自检使用）
        /// <para>
        /// 自检必须能指向临时文件：否则 <c>--SelfTest</c> 一旦在部署目录被执行，
        /// 就会把用户真实的 dnsendpoints.json 覆盖成测试数据。
        /// </para>
        /// </summary>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        /// <param name="dataFile">数据文件名</param>
        internal PersistenceService(
            FastGithubConfig fastGithubConfig,
            ILogger<PersistenceService> logger,
            string dataFile)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
            this.dataFile = dataFile;
            this.tempFile = dataFile + ".tmp";
        }


        /// <summary>
        /// 读取保存的节点（含候选IP）
        /// </summary>
        /// <returns>读不到或格式不合法时返回空列表，绝不抛异常</returns>
        public IList<PersistedEndPoint> ReadEndPoints()
        {
            if (File.Exists(dataFile) == false)
            {
                return Array.Empty<PersistedEndPoint>();
            }

            try
            {
                dataLocker.Wait();

                var utf8Json = File.ReadAllBytes(dataFile);
                var endPointItems = JsonSerializer.Deserialize(utf8Json, EndPointItemsContext.Default.EndPointItemArray);
                if (endPointItems == null)
                {
                    return Array.Empty<PersistedEndPoint>();
                }

                var endPoints = new List<PersistedEndPoint>(endPointItems.Length);
                foreach (var item in endPointItems)
                {
                    if (string.IsNullOrWhiteSpace(item.Host) == true ||
                        this.fastGithubConfig.IsMatch(item.Host) == false)
                    {
                        continue;
                    }

                    var addresses = new List<PersistedAddress>();
                    foreach (var addressItem in item.Addresses ?? Array.Empty<EndPointAddressItem>())
                    {
                        if (IPAddress.TryParse(addressItem.Address, out var address) == false)
                        {
                            continue;
                        }
                        if (IpAddressFilter.IsInvalid(address))
                        {
                            continue;
                        }
                        // 负数/非数是历史脏数据，按"未知"处理（TimeSpan.Zero 会在排序时排在前面，
                        // 所以用 MaxValue 之外的哨兵不必要 —— 上层只用它做初值，真实探测会覆盖）
                        var elapsed = double.IsNaN(addressItem.ElapsedMilliseconds) || addressItem.ElapsedMilliseconds <= 0
                            ? TimeSpan.Zero
                            : TimeSpan.FromMilliseconds(addressItem.ElapsedMilliseconds);
                        addresses.Add(new PersistedAddress(address, elapsed));
                    }

                    endPoints.Add(new PersistedEndPoint(
                        new DnsEndPoint(item.Host, item.Port),
                        addresses));
                }
                return endPoints;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "读取dns记录异常");
                return Array.Empty<PersistedEndPoint>();
            }
            finally
            {
                dataLocker.Release();
            }
        }

        /// <summary>
        /// 保存节点到文件（含候选IP）
        /// <para>
        /// 先写临时文件再原子替换：写一半被杀掉时不会留下半截 JSON 把下次启动的
        /// 候选池清空（这是"持久化反而更慢"的经典坑）。
        /// </para>
        /// </summary>
        /// <param name="endPoints"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task WriteEndPointsAsync(IEnumerable<PersistedEndPoint> endPoints, CancellationToken cancellationToken)
        {
            try
            {
                await dataLocker.WaitAsync(CancellationToken.None);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var endPointItems = endPoints
                    .Where(item => item.Addresses.Count > 0)
                    .Select(item => new EndPointItem(
                        item.EndPoint.Host,
                        item.EndPoint.Port,
                        item.Addresses.Select(address => new EndPointAddressItem(
                            address.Address.ToString(),
                            Math.Round(address.Elapsed.TotalMilliseconds, 1))).ToArray(),
                        now))
                    .ToArray();

                var utf8Json = JsonSerializer.SerializeToUtf8Bytes(endPointItems, EndPointItemsContext.Default.EndPointItemArray);
                await File.WriteAllBytesAsync(tempFile, utf8Json, cancellationToken);
                File.Move(tempFile, dataFile, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                // 停机取消：保留上一次的完整文件即可，不记告警
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "保存dns记录异常");
            }
            finally
            {
                dataLocker.Release();
            }
        }
    }
}
