using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private readonly int httpProxyPort;

        /// <summary>
        /// tcp拦截后台服务
        /// </summary>
        /// <param name="tcpInterceptors"></param>
        /// <param name="logger"></param>
        /// <param name="options"></param>
        public TcpInterceptHostedService(
            IEnumerable<ITcpInterceptor> tcpInterceptors,
            ILogger<TcpInterceptHostedService> logger,
            IOptions<FastGithubOptions> options)
        {
            this.tcpInterceptors = tcpInterceptors;
            this.logger = logger;

            // 读实际配置端口，而不是硬编码常量：降级提示必须与真实配置一致
            this.httpProxyPort = options.Value.HttpProxyPort;
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

                // 与 dns 拦截器同理：只有正向代理真的在监听，这个指引才成立
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
