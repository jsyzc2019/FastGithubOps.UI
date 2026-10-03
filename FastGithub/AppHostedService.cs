using FastGithub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// app后台服务
    /// </summary>
    sealed class AppHostedService : BackgroundService
    {
        private readonly IHost host;
        private readonly IOptions<AppOptions> appOptions;
        private readonly IOptions<FastGithubOptions> fastGithubOptions;
        private readonly ILogger<AppHostedService> logger;

        public AppHostedService(
            IHost host,
            IOptions<AppOptions> appOptions,
            IOptions<FastGithubOptions> fastGithubOptions,
            ILogger<AppHostedService> logger)
        {
            this.host = host;
            this.appOptions = appOptions;
            this.fastGithubOptions = fastGithubOptions;
            this.logger = logger;
        }

        /// <summary>
        /// 启动完成
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            Version version;
            if (ProductionVersion.Current is not null)
            {
                version = ProductionVersion.Current.Version;
            }
            else
            {
                version = new Version(0, 0);
            }
            this.logger.LogInformation($"\n======[ {nameof(FastGithub)} 启动完成，当前版本为 V{version} ]======\n");
            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// 后台任务
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1d), stoppingToken);
            await this.CheckFastGithubProxyAsync(stoppingToken);
            await this.WaitForParentProcessExitAsync(stoppingToken);
        }


        /// <summary>
        /// 检测fastgithub代理设置
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task CheckFastGithubProxyAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows() == false)
            {
                try
                {
                    if (await this.UseFastGithubProxyAsync() == false)
                    {
                        var httpProxyPort = this.fastGithubOptions.Value.HttpProxyPort;
                        this.logger.LogWarning($"请设置系统自动代理为http://{IPAddress.Loopback}:{httpProxyPort}，或手动代理http/https为{IPAddress.Loopback}:{httpProxyPort}");
                    }
                }
                catch (Exception)
                {
                    this.logger.LogWarning("尝试获取代理信息失败");
                }
            }
        }


        /// <summary>
        /// 应用fastgithub代理
        /// </summary>
        /// <param name="proxyServer"></param>
        /// <param name="httpProxyPort"></param>
        /// <returns></returns>
        private async Task<bool> UseFastGithubProxyAsync()
        {
            var systemProxy = HttpClient.DefaultProxy;
            if (systemProxy == null)
            {
                return false;
            }

            var domain = this.fastGithubOptions.Value.DomainConfigs.Keys.FirstOrDefault();
            if (domain == null)
            {
                return true;
            }

            var destination = new Uri($"https://{domain.Replace('*', 'a')}");
            var proxyServer = systemProxy.GetProxy(destination);
            if (proxyServer == null)
            {
                return false;
            }

            var httpProxyPort = this.fastGithubOptions.Value.HttpProxyPort;
            if (proxyServer.Port != httpProxyPort)
            {
                return false;
            }

            if (IPAddress.TryParse(proxyServer.Host, out var address))
            {
                return IPAddress.IsLoopback(address);
            }

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(proxyServer.Host);
                return addresses.Any(item => IPAddress.IsLoopback(item));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 等待父进程退出
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task WaitForParentProcessExitAsync(CancellationToken cancellationToken)
        {
            var parentId = this.appOptions.Value.ParentProcessId;
            if (parentId <= 0)
            {
                return;
            }

            var exited = false;
            try
            {
                using var process = Process.GetProcessById(parentId);

                // 【v2.6.4 修复】原实现用 WaitForExit() 无限阻塞，且没有任何日志。
                // 改为轮询：既能感知退出，也能在日志里留下"父进程已退出"的明确记录，
                // 让"托盘关掉后主程序还在拦截"这类问题有据可查。
                while (cancellationToken.IsCancellationRequested == false)
                {
                    if (process.HasExited)
                    {
                        exited = true;
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500d), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 整体停机中，属正常路径
                return;
            }
            catch (ArgumentException)
            {
                // 进程已不存在（GetProcessById 抛"找不到进程"），等同于已退出。
                // 这是最常见的情形：UI 被任务管理器强杀后进程立即消失。
                exited = true;
            }
            catch (Exception ex)
            {
                // 【v2.6.4 修复】原为 LogError，但"查父进程失败"多是权限或进程已消失等
                // 正常情况，记 Error 会让用户误以为程序出了故障。
                // 降为 Warning：仍需可见，但不制造恐慌。
                this.logger.LogWarning(ex, $"获取进程{parentId}异常，按已退出处理");
                exited = true;
            }

            if (exited)
            {
                this.logger.LogInformation($"检测到父进程 {parentId} 已退出，正在主动关闭主程序");
                await this.host.StopAsync(cancellationToken);
            }
        }

    }
}
