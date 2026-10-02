using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept
{
    /// <summary>
    /// tcp拦截后台服务
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class TcpInterceptHostedService : BackgroundService
    {
        private readonly IEnumerable<ITcpInterceptor> tcpInterceptors;
        private readonly ILogger<TcpInterceptHostedService> logger;
        private readonly IHost host;

        /// <summary>
        /// tcp拦截后台服务
        /// </summary>
        /// <param name="tcpInterceptors"></param>
        /// <param name="logger"></param>
        /// <param name="host"></param>
        public TcpInterceptHostedService(
            IEnumerable<ITcpInterceptor> tcpInterceptors,
            ILogger<TcpInterceptHostedService> logger,
            IHost host)
        {
            this.tcpInterceptors = tcpInterceptors;
            this.logger = logger;
            this.host = host;
        }

        /// <summary>
        /// https后台
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var tasks = this.tcpInterceptors.Select(item => item.InterceptAsync(stoppingToken));
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 995)
            {
            }
            catch (Exception ex)
            {
                // 与 dns 拦截器同理：拦截失败不再拖垮整个进程，降级为正向代理模式存活
                this.logger.LogError(ex, "tcp拦截器异常，透明拦截模式已失效");
                this.logger.LogWarning($"请将系统代理设置为 http://127.0.0.1:{FastGithubOptions.DefaultHttpProxyPort} 后继续使用（PAC地址见启动日志）");
            }
        }
    }
}
