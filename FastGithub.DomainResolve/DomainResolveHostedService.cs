using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析后台服务
    /// </summary>
    sealed class DomainResolveHostedService : BackgroundService
    {
        /// <summary>
        /// 测速周期（秒）。提为常量供 CandidatePolicySelfTest 断言下界，
        /// 防止将来被误改回 1 秒而把 A1 的收益悄悄抹掉。
        /// </summary>
        internal const int TEST_PERIOD_SECONDS = 60;

        private readonly DnscryptProxy dnscryptProxy;
        private readonly IDomainResolver domainResolver;
        private readonly HostsService hostsService;
        private readonly ILogger<DomainResolveHostedService> logger;
        private readonly TimeSpan dnscryptProxyInitDelay = TimeSpan.FromSeconds(5d);
        /// <summary>
        /// 测速周期。
        /// <para>
        /// 【v2.6.13 A1，借鉴上游 v2.5.0 + 本仓日志实证】原值 1 秒：等于每秒对
        /// 全部域名×全部候选 IP 做一轮解析/握手探测。本仓 10-21~10-28 日志实测：
        /// DomainResolver 解析行 2.4~2.8 万条/天（约 1000/小时）、DoH 解析失败率
        /// 15~22%、"所有端点均不可用"约 1000 次/天、明文 DNS(TCP:53) 被 RST
        /// 约 1500 次/天，且**全天均匀无时间聚集、无 ERR** —— 这正是持续自造
        /// 负载的特征，而非纯外部网络波动（上游 v2.5.0 用完全相同的原因描述
        /// 把 1 秒改为 60 秒，并实测"公共 DNS 连续查询失败被熔断"随之消失）。
        /// </para>
        /// <para>
        /// 改 60 秒不会拖慢故障恢复：IP 被拉黑后的重选由
        /// <c>HttpClientHandler.ReportFailure → DomainResolver.RefreshAsync</c>
        /// 即时触发（10 秒冷却），不依赖本轮询；DoH 正缓存 10 分钟、
        /// IPAddressService 探测缓存 5 分钟，高频重测也拿不到新结论。
        /// hosts 源刷新周期（1 小时）与本周期独立，不受影响。
        /// </para>
        /// </summary>
        private readonly TimeSpan testPeriodTimeSpan = TimeSpan.FromSeconds(TEST_PERIOD_SECONDS);
        private readonly TimeSpan hostsRefreshPeriodTimeSpan = TimeSpan.FromHours(1d);

        /// <summary>
        /// 域名解析后台服务
        /// </summary>
        /// <param name="dnscryptProxy"></param>
        /// <param name="domainResolver"></param>
        /// <param name="hostsService"></param>
        public DomainResolveHostedService(
            DnscryptProxy dnscryptProxy,
            IDomainResolver domainResolver,
            HostsService hostsService,
            ILogger<DomainResolveHostedService> logger)
        {
            this.dnscryptProxy = dnscryptProxy;
            this.domainResolver = domainResolver;
            this.hostsService = hostsService;
            this.logger = logger;
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnscryptProxy.StartAsync(stoppingToken);
                await Task.Delay(dnscryptProxyInitDelay, stoppingToken);

                var lastHostsRefresh = default(DateTime);
                while (stoppingToken.IsCancellationRequested == false)
                {
                    await this.domainResolver.TestSpeedAsync(stoppingToken);

                    // 周期性刷新在线hosts源
                    if (DateTime.Now - lastHostsRefresh >= this.hostsRefreshPeriodTimeSpan)
                    {
                        lastHostsRefresh = DateTime.Now;
                        await this.hostsService.RefreshAsync(stoppingToken);
                    }

                    await Task.Delay(this.testPeriodTimeSpan, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "域名解析异常");
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            this.dnscryptProxy.Stop();
            return base.StopAsync(cancellationToken);
        }
    }
}
