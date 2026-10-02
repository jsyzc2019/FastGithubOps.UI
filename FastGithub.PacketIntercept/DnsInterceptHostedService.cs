using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private readonly int httpProxyPort;

        /// <summary>
        /// dns拦截后台服务
        /// </summary>
        /// <param name="dnsInterceptor"></param>
        /// <param name="conflictSolvers"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        public DnsInterceptHostedService(
            IDnsInterceptor dnsInterceptor,
            IEnumerable<IDnsConflictSolver> conflictSolvers,
            ILogger<DnsInterceptHostedService> logger,
            IOptions<FastGithubOptions> options)
        {
            this.dnsInterceptor = dnsInterceptor;
            this.conflictSolvers = conflictSolvers;
            this.logger = logger;

            // 读实际配置端口，而不是硬编码常量：用户改 appsettings.json 后
            // 提示里的端口必须跟着变，否则会把用户指引到一个没人监听的端口
            this.httpProxyPort = options.Value.HttpProxyPort;
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

                // 仅在正向代理真的监听成功时才给出该指引；
                // 若端口被占用，ListenHttpProxy 会直接返回且不注册任何端点，
                // 此时提示该端口等于把用户导向一个空端口。
                var listenedPort = HttpProxyRuntimeState.ListenedPort;
                if (listenedPort != null)
                {
                    this.logger.LogWarning(
                        $"请将系统代理设置为 http://127.0.0.1:{listenedPort} 后继续使用（PAC地址见启动日志）");
                }
                else
                {
                    this.logger.LogWarning(
                        $"正向代理也未启动（tcp端口{this.httpProxyPort}被占用），" +
                        $"请修改配置文件中 {nameof(FastGithubOptions.HttpProxyPort)} 换一个空闲端口后重启");
                }
            }
        }
    }
}
