using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 在线hosts源解析服务
    /// 定时从远程hosts地址拉取“域名->IP”映射，作为DNS解析之外的额外候选IP来源
    /// </summary>
    sealed class HostsService
    {
        private readonly HttpClient httpClient;
        private readonly IOptionsMonitor<FastGithubOptions> options;
        private readonly ILogger<HostsService> logger;
        private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> mapping = new();

        /// <summary>
        /// 并发拉取合流（single-flight）。
        /// <para>
        /// 【v2.6.6 P1-2】<c>RefreshAsync</c> 有 4 个调用方：后台 1 小时定时、
        /// 健康度触发的 <c>DomainResolver.RefreshAsync</c>、UI 的 hosts-only 刷新，
        /// 以及 IP 拉黑后的恢复刷新。其中恢复路径在多个域名同时被拉黑时会并发进入
        /// （原实现只有"每域名 10 秒冷却"，**没有全局节流**），
        /// 于是多个拉取同时打向同一个源 —— 既浪费带宽，也让源看到的请求形态像流量注入。
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim refreshGate = new(1, 1);

        /// <summary>
        /// 上次**成功**拉取的时刻。
        /// <para>成功的拉取内容等价，短时间内重复拉取毫无收益；只有失败才值得尽快重试。</para>
        /// </summary>
        private DateTime lastSuccessAt = DateTime.MinValue;

        /// <summary>
        /// 上次**尝试**拉取的时刻（无论成败），用于计算失败退避是否已过。
        /// </summary>
        private DateTime lastAttemptAt = DateTime.MinValue;

        /// <summary>
        /// 成功拉取后的最小间隔：5 分钟。
        /// <para>原实现无任何节流，后台 1 小时定时与健康度恢复路径叠加会重复拉取。</para>
        /// </summary>
        private static readonly TimeSpan successRefreshInterval = TimeSpan.FromMinutes(5d);

        /// <summary>
        /// 连续失败次数，用于在源长期不可用时给出明确告警而非静默失效
        /// </summary>
        private int consecutiveFailures;

        /// <summary>
        /// 失败后的重试间隔基数，随连续失败次数指数增长（5min → 10min → 20min …，封顶 2h）。
        /// <para>
        /// 【为什么必须有】原实现失败后完全没有节流：后台 1 小时定时与健康度恢复路径叠加，
        /// 同一个失效源会被反复拉取，每轮还白等满超时。
        /// v2.6.5 长时日志实测失败原因是「不知道这样的主机」(raw.hellogithub.com:443)，
        /// 即本机明文 53被 RST 导致解析不出该域名 —— 这类失败**不会自行好转**，
        /// 密集重试既无收益，又把真正的故障原因淹没在重复告警里。
        /// </para>
        /// </summary>
        private static readonly TimeSpan baseFailureRetry = TimeSpan.FromMinutes(5d);

        /// <summary>失败重试间隔上限</summary>
        private static readonly TimeSpan maxFailureRetry = TimeSpan.FromHours(2d);

        /// <summary>
        /// 拉取超时预算。实测该源响应在数百毫秒量级，30s 足以覆盖抖动；
        /// 原值 10s 在网络拥塞时会把"慢但成功"误判为失败，白白触发一轮失败退避。
        /// </summary>
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30d);

        /// <summary>
        /// 累计被丢弃的无效 IP 数量（环回/内网/污染段）。
        /// 源里出现这类地址说明源本身已被污染或格式异常，需明确告警而非静默丢弃——
        /// 否则表现只是"某些域名不通"，几乎无法定位。
        /// </summary>
        private int invalidIpCount;

        /// <summary>
        /// 在线hosts源解析服务
        /// </summary>
        /// <param name="options"></param>
        /// <param name="logger"></param>
        public HostsService(
            IOptionsMonitor<FastGithubOptions> options,
            ILogger<HostsService> logger)
        {
            this.options = options;
            this.logger = logger;

            // 【v2.6.6 P1-2】显式禁用系统代理，与 DoH 通道（同样 UseProxy=false）保持一致。
            // 原实现走默认代理设置：本机若配置了代理环境变量，"源不可达"可能只是代理拒绝，
            // 与 DoH 通道的直连结论互相矛盾 —— 这正是"同一个域名，两个通道结论不一致"的经典陷阱。
            this.httpClient = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            })
            {
                Timeout = HttpTimeout
            };
        }

        /// <summary>
        /// 刷新hosts映射，拉取远程hosts源并解析为“域名->IP”缓存
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <param name="force">
        /// 是否忽略成功节流与失败退避。
        /// <para>用于 UI 上的「手动刷新」：那是用户显式动作，
        /// 语义上必须"现在就去拉"，不能被后台节流挡住；
        /// 否则用户点��刷新却毫无反应，会被当成程序卡死。</para>
        /// </param>
        /// <returns></returns>
        public async Task RefreshAsync(CancellationToken cancellationToken, bool force = false)
        {
            var hostsUrl = this.options.CurrentValue.HostsUrl?.Trim();
            if (string.IsNullOrEmpty(hostsUrl))
            {
                // 显式禁用在线hosts源，只依赖本机DNS解析
                if (this.mapping.IsEmpty == false)
                {
                    this.mapping.Clear();
                }
                return;
            }

            // 【v2.6.6 P1-2】全局节流：成功拉取的内容等价，5 分钟内的重复请求一律跳过。
            // 恢复路径（多个域名同时被拉黑）会在很短时间内多次进入本方法，
            // 原实现每次都真打一次源 —— 既无收益，也让源看到的请求形态异常。
            if (force == false &&
                this.lastSuccessAt != DateTime.MinValue &&
                DateTime.UtcNow - this.lastSuccessAt < successRefreshInterval)
            {
                return;
            }

            // 合流：并发进入者等待首个请求的结果，而不是各自发一次。
            if (await this.refreshGate.WaitAsync(TimeSpan.FromSeconds(5d), cancellationToken) == false)
            {
                this.logger.LogDebug("在线hosts源刷新正在进行中，本次跳过");
                return;
            }

            try
            {
                // 二次检查：等锁期间可能已有别人成功拉取过了。
                if (force == false &&
                    this.lastSuccessAt != DateTime.MinValue &&
                    DateTime.UtcNow - this.lastSuccessAt < successRefreshInterval)
                {
                    return;
                }

                // 失败退避：连续失败越多，等得越久（指数增长，封顶 2h）。
                // 这类失败（本机 DNS 解析不出该域名）不会自行好转，密集重试毫无意义。
                if (force == false && this.consecutiveFailures > 0)
                {
                    var wait = GetFailureRetryDelay(this.consecutiveFailures);
                    var elapsed = DateTime.UtcNow - this.lastAttemptAt;
                    if (elapsed < wait)
                    {
                        this.logger.LogDebug(
                            $"在线hosts源处于失败退避中（已连续失败 {this.consecutiveFailures} 次，" +
                            $"{(wait - elapsed).TotalMinutes:F1} 分钟后重试），本次跳过");
                        return;
                    }
                }

                this.lastAttemptAt = DateTime.UtcNow;

                var content = await this.httpClient.GetStringAsync(hostsUrl, cancellationToken);
                var map = Parse(content, out var invalidCount);
                this.invalidIpCount = invalidCount;

                // 【关键】只有拿到**非空且可解析**的内容才替换映射。
                // 原实现无条件先 Clear 再逐条填入：一旦源返回空文件或被劫持成空内容，
                // 已有映射会被清空，程序立刻失去这条候选来源且无从恢复（要等下次成功拉取）。
                if (map.Count > 0)
                {
                    this.mapping.Clear();
                    foreach (var item in map)
                    {
                        this.mapping[item.Key] = item.Value;
                    }
                }
                else
                {
                    this.logger.LogWarning(
                        $"在线hosts源 {hostsUrl} 返回空内容或全部条目无法解析，" +
                        "已保留上一次成功拉取的映射（如从未成功过则该源暂不提供候选IP）。");
                }

                this.consecutiveFailures = 0;
                this.lastSuccessAt = DateTime.UtcNow;
                this.logger.LogInformation($"已更新在线hosts解析，共{this.mapping.Count}个域名");

                if (this.invalidIpCount > 0)
                {
                    this.logger.LogWarning(
                        $"在线hosts源中有 {this.invalidIpCount} 个无效IP（127.0.0.1 等环回地址、" +
                        "私有网段或已知污染地址）已被丢弃。这类地址若被当作候选会把请求导向本机或错误目标。" +
                        "如持续出现，说明该源已被污染，建议更换（FastGithub:HostsUrl）。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 整体停机：不是源的问题，不计失败、不告警。
                this.logger.LogDebug("在线hosts源刷新因程序停止而取消");
            }
            catch (Exception ex)
            {
                this.consecutiveFailures++;

                // 【保留旧映射】失败时**不**清空 mapping：一份几分钟前的映射
                // 远比没有映射有用（DoH 是主路径，hosts 只是额外候选）。
                // 但必须让用户知道当前用的是旧数据 —— 静默沿用会让人以为"源是好的"。
                this.logger.LogWarning(
                    $"在线hosts源更新失败（第 {this.consecutiveFailures} 次，保留上次成功拉取的 " +
                    $"{this.mapping.Count} 个域名映射）：{ex.Message}");

                if (this.consecutiveFailures == 3)
                {
                    this.logger.LogError(
                        $"在线hosts源 {hostsUrl} 已连续 {this.consecutiveFailures} 次不可用。" +
                        "该地址返回的内容会被本程序直接当作域名->IP映射使用，" +
                        "请确认该域名仍由可信方长期维护；" +
                        "如需更换或禁用，可在 appsettings.json 中设置 FastGithub:HostsUrl。");
                }
            }
            finally
            {
                this.refreshGate.Release();
            }
        }

        /// <summary>
        /// 失败重试间隔：5min 起指数增长，封顶 <see cref="maxFailureRetry"/>。
        /// </summary>
        private static TimeSpan GetFailureRetryDelay(int failures)
        {
            // 位移上限 10 位，防止连续失败上万次后整数溢出成负数。
            var shift = Math.Min(failures - 1, 10);
            var scaled = baseFailureRetry * (1 << shift);
            return scaled > maxFailureRetry ? maxFailureRetry : scaled;
        }

        /// <summary>
        /// 获取指定域名在hosts源中的IP地址
        /// </summary>
        /// <param name="host">域名</param>
        /// <param name="addresses">命中的IP列表</param>
        /// <returns></returns>
        public bool TryGetAddresses(string host, out IReadOnlyList<IPAddress> addresses)
        {
            return this.mapping.TryGetValue(host, out addresses!);
        }

        /// <summary>
        /// 指定域名是否被hosts源覆盖
        /// </summary>
        /// <param name="host">域名</param>
        /// <returns></returns>
        public bool IsCovered(string host)
        {
            return this.mapping.TryGetValue(host, out var addresses) && addresses.Count > 0;
        }

        /// <summary>
        /// 解析hosts文本内容
        /// </summary>
        /// <param name="content">hosts文本</param>
        /// <param name="invalidCount">被丢弃的无效IP 数量（环回/内网/污染段）</param>
        /// <returns>域名->IP映射</returns>
        private static Dictionary<string, IReadOnlyList<IPAddress>> Parse(string content, out int invalidCount)
        {
            var map = new Dictionary<string, List<IPAddress>>();
            invalidCount = 0;
            foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || IPAddress.TryParse(parts[0], out var ip) == false)
                {
                    continue;
                }

                // 【关键】过滤环回/内网/保留段/已知污染地址。
                // 原实现只做 TryParse，导致源里出现的 127.0.0.1、192.168.x.x、
                // 或污染地址会被原样当作候选 IP，且hosts 源在 DnsClient 中优先级最高
                // （排在 DoH 与 DNS 之前），于是它会直接占据首选位置——
                // 实际日志已出现 "avatars.githubusercontent.com->127.0.0.1"，
                // 请求被导向本机回环，表现为"域名完全不通"且原因极难定位。
                if (IpAddressFilter.IsInvalid(ip))
                {
                    invalidCount++;
                    continue;
                }

                var host = parts[1].Trim().ToLowerInvariant();
                if (map.TryGetValue(host, out var list) == false)
                {
                    list = new List<IPAddress>();
                    map[host] = list;
                }
                if (list.Contains(ip) == false)
                {
                    list.Add(ip);
                }
            }

            return map.ToDictionary(item => item.Key, item => (IReadOnlyList<IPAddress>)item.Value);
        }
    }
}