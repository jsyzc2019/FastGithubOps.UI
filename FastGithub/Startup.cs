using FastGithub.Configuration;
using FastGithub.DomainResolve;
using FastGithub.FlowAnalyze;
using FastGithub.Diagnostics;
using FastGithub.HttpServer.Certs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace FastGithub
{
    /// <summary>
    /// 启动项
    /// </summary>
    static class Startup
    {
        /// <summary>
        /// 配置通用主机
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureHost(this WebApplicationBuilder builder)
        {
            builder.Host.UseSystemd().UseWindowsService();
            builder.Host.UseSerilog((hosting, logger) =>
            {
                var template = "{Timestamp:O} [{Level:u3}]{NewLine}{SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}";
                logger
                    .ReadFrom.Configuration(hosting.Configuration)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(outputTemplate: template)
                    .WriteTo.File(Path.Combine("logs", @"log.txt"), rollingInterval: RollingInterval.Day, outputTemplate: template);

                var udpLoggerPort = hosting.Configuration.GetValue(nameof(AppOptions.UdpLoggerPort), 38457);
                logger.WriteTo.UDPSink(IPAddress.Loopback, udpLoggerPort);
            });
        }

        /// <summary>
        /// 配置web主机
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureWebHost(this WebApplicationBuilder builder)
        {
            // 【v2.6.4 修复】原为 TimeSpan.FromSeconds(1d)（1 天），实际后果是：
            // 停机时 Host 会为每个正在处理中的请求最多等 1 天才放弃，
            // 于是"关闭应用"后主进程可能长时间不退出、端口不释放、WinDivert 持续拦截。
            // 用户观感是"关了还在生效，重开又报端口占用"。
            // 取 5 秒：足够让在途请求正常收尾，又保证停机是确定性的。
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(5d));
            builder.WebHost.UseKestrel(kestrel =>
            {
                kestrel.NoLimit();
                if (OperatingSystem.IsWindows())
                {
                    kestrel.ListenHttpsReverseProxy();
                    kestrel.ListenHttpReverseProxy();
                    kestrel.ListenSshReverseProxy();
                    kestrel.ListenGitReverseProxy();

                    // 借鉴 dev-sidecar 的系统代理模式：Windows 上额外监听一个正向 http 代理。
                    // 透明拦截依赖 WinDivert，一旦驱动被杀软拦截/未以管理员运行/与其它 WinDivert 程序冲突，
                    // 原来的实现会直接退出，用户完全没有退路；这里保留一条可用通道。
                    kestrel.ListenHttpProxy(throwOnPortOccupied: false);
                }
                else
                {
                    kestrel.ListenHttpProxy();
                }
                kestrel.ListenLocalhost(GlobalListener.UiHttpPort);
            });
        }


        /// <summary>
        /// 配置配置
        /// </summary>
        /// <param name="builder"></param>
        public static void ConfigureConfiguration(this WebApplicationBuilder builder)
        {
            const string APPSETTINGS = "appsettings";
            if (Directory.Exists(APPSETTINGS) == true)
            {
                foreach (var file in Directory.GetFiles(APPSETTINGS, "appsettings.*.json"))
                {
                    var jsonFile = Path.Combine(APPSETTINGS, Path.GetFileName(file));
                    builder.Configuration.AddJsonFile(jsonFile, true, true);
                }
            }
        }


        /// <summary>
        /// 配置服务
        /// </summary>
        /// <param name="builder"></param>
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Dictionary<string, DomainConfig>))]
        public static void ConfigureServices(this WebApplicationBuilder builder)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;

            services.Configure<AppOptions>(configuration);
            services.Configure<FastGithubOptions>(configuration.GetSection(nameof(FastGithub)));

            services.AddConfiguration();
            services.AddDomainResolve();
            services.AddHttpClient();
            services.AddReverseProxy();
            services.AddFlowAnalyze();
            services.AddSingleton<ConnectivityProbe>();
            services.AddHostedService<AppHostedService>();

            if (OperatingSystem.IsWindows())
            {
                services.AddPacketIntercept();
            }
        }

        /// <summary>
        /// 配置应用
        /// </summary>
        /// <param name="app"></param>
        public static void ConfigureApp(this WebApplication app)
        {
            app.UseHttpProxyPac();
            app.UseRequestLogging();
            app.UseHttpReverseProxy();

            app.UseRouting();
            app.DisableRequestLogging();

            app.MapGet("/flowStatistics", context =>
            {
                var flowStatistics = context.RequestServices.GetRequiredService<IFlowAnalyzer>().GetFlowStatistics();
                var json = JsonSerializer.Serialize(flowStatistics, FlowStatisticsContext.Default.FlowStatistics);
                return context.Response.WriteAsync(json);
            });

            // 触发IP刷新：拉取在线hosts源并解析测速（不发起DNS查询）
            app.MapGet("/refresh-ip", async context =>
            {
                var resolver = context.RequestServices.GetRequiredService<IDomainResolver>();
                await resolver.RefreshHostsAsync(context.RequestAborted);
                context.Response.ContentType = "text/plain;charset=utf-8";
                await context.Response.WriteAsync("IP更新已触发");
            });

            // 【v2.6.4 新增】UI 退出时显式通知主程序停机。
            // <para>
            // 原来 UI 关闭只 Dispose 自己的托盘图标，主程序完全不知情：
            // 它只能靠 <c>WaitForParentProcessExitAsync</c> 轮询父进程来察觉，
            // 而 UI 被任务管理器强杀、或 UI 自身异常崩溃时这条链路并不可靠，
            // 结果是主程序变成孤儿进程——**WinDivert 仍在拦截网络流量**，
            // 用户以为已经关掉，实际上 GitHub 访问路径仍被接管，且端口不释放、
            // 下次启动直接报端口占用。
            // </para>
            // <para>
            // 只监听在 localhost 的 UI 内部端口上（<c>ListenLocalhost</c>），
            // 不对外暴露；先回响应再异步停机，避免 UI 的请求被自己的停机动作卡住。
            // </para>
            app.MapGet("/shutdown", async context =>
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Shutdown");
                var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();

                logger.LogInformation("收到 UI 的停机请求，正在关闭主程序");
                context.Response.ContentType = "text/plain;charset=utf-8";
                await context.Response.WriteAsync("主程序正在退出");

                // 先让响应发出去，再触发停机；否则客户端会收到连接被重置。
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 给响应一点时间写出去
                        await Task.Delay(TimeSpan.FromMilliseconds(200d));
                        lifetime.StopApplication();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "停机时发生异常");
                    }
                });
            });

            // 诊断：一次看清"证书是否真的被信任"与"IP健康度闭环是否在工作"。
            // 这两个状态失效时的外部表现都只是"网络不好"，没有这个端点就只能靠猜。
            app.MapGet("/diagnostics", context =>
            {
                var certService = context.RequestServices.GetRequiredService<CertService>();
                var resolver = context.RequestServices.GetRequiredService<IDomainResolver>();
                var options = context.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;

                var report = new Dictionary<string, object?>
                {
                    ["caCert"] = new
                    {
                        path = certService.CaCerFilePath,
                        trusted = certService.CheckCaCertTrusted()
                    },
                    ["listeners"] = new
                    {
                        uiHttpPort = GlobalListener.UiHttpPort,
                        udpLoggerPort = options.UdpLoggerPort,
                        httpPort = GlobalListener.HttpPort,
                        httpsPort = GlobalListener.HttpsPort,
                        sshPort = GlobalListener.SshPort,
                        gitPort = GlobalListener.GitPort,
                        httpProxyPort = Configuration.HttpProxyRuntimeState.ListenedPort
                    },
                    ["ipHealth"] = new
                    {
                        unhealthyCount = resolver.GetIpHealth(includeHealthy: false, maxCount: int.MaxValue).Count,
                        items = resolver.GetIpHealth(includeHealthy: false, maxCount: 30)
                    }
                };

                context.Response.ContentType = "application/json;charset=utf-8";
                return context.Response.WriteAsJsonAsync(report, context.RequestAborted);
            });

            // 实时连通性探测：主动对 GitHub 各域名做 TCP+TLS 握手并计时。
            // 用来回答"现在到底通不通、走哪个IP最快"——健康度统计只反映历史成败，
            // 无法反映当前链路质量，而判断"加速有没有生效"恰恰需要后者。
            app.MapGet("/connectivity", async context =>
            {
                var probe = context.RequestServices.GetRequiredService<ConnectivityProbe>();

                var hosts = ConnectivityProbe.DefaultHosts;
                var requested = context.Request.Query["host"];
                if (string.IsNullOrWhiteSpace(requested) == false)
                {
                    hosts = requested.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                var results = await probe.ProbeAsync(hosts, context.RequestAborted);

                context.Response.ContentType = "application/json;charset=utf-8";
                await context.Response.WriteAsJsonAsync(new
                {
                    reachableCount = results.Count(item => item.Reachable),
                    totalCount = results.Count,
                    items = results
                }, context.RequestAborted);
            });
        }
    }
}
