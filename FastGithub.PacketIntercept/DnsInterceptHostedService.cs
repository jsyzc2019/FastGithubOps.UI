using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept
{
    /// <summary>
    /// dns拦截后台服务
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class DnsInterceptHostedService : BackgroundService
    {
        private readonly IDnsInterceptor dnsInterceptor;
        private readonly IEnumerable<IDnsConflictSolver> conflictSolvers;
        private readonly ILogger<DnsInterceptHostedService> logger;
        private readonly IHost host;

        /// <summary>
        /// dns拦截后台服务
        /// </summary>
        /// <param name="dnsInterceptor"></param>
        /// <param name="conflictSolvers"></param>
        /// <param name="logger"></param>
        /// <param name="host"></param>
        public DnsInterceptHostedService(
            IDnsInterceptor dnsInterceptor,
            IEnumerable<IDnsConflictSolver> conflictSolvers,
            ILogger<DnsInterceptHostedService> logger,
            IHost host)
        {
            this.dnsInterceptor = dnsInterceptor;
            this.conflictSolvers = conflictSolvers;
            this.logger = logger;
            this.host = host;
        }

        /// <summary>
        /// 启动时处理冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StartAsync(CancellationToken cancellationToken)
        {          
            foreach (var solver in this.conflictSolvers)
            {
                await solver.SolveAsync(cancellationToken);
            }
            await base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 停止时恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var solver in this.conflictSolvers)
            {
                await solver.RestoreAsync(cancellationToken);
            }
            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// dns后台
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await this.dnsInterceptor.InterceptAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 995)
            {
            }
            catch (Exception ex)
            {
                // 原实现在这里直接 StopAsync 结束整个进程：WinDivert 一旦被杀软拦截、
                // 未以管理员运行或与其它 WinDivert 程序冲突，用户就完全失去加速能力。
                // 改为降级存活：透明拦截失效时，仍保留 127.0.0.1:{HttpProxyPort} 的正向代理可用。
                this.logger.LogError(ex, "dns拦截器异常，透明拦截模式已失效");
                this.logger.LogWarning($"请将系统代理设置为 http://127.0.0.1:{FastGithubOptions.DefaultHttpProxyPort} 后继续使用（PAC地址见启动日志）");
            }
        }
    }
}
