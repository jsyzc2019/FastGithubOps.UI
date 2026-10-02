using FastGithub.Configuration;
using FastGithub.DomainResolve;
using FastGithub.FlowAnalyze;
using FastGithub.HttpServer.Certs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Text.Json;

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
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(1d));
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
        }
    }
}
