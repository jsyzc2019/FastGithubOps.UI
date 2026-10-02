using FastGithub.Configuration;
using Microsoft.AspNetCore.Builder;
using System;
using System.IO;

namespace FastGithub
{

    class Program
    {
        /// <summary>
        /// 程序入口
        /// </summary>
        /// <param name="args"></param>
        public static void Main(string[] args)
        {
            ConsoleUtil.DisableQuickEdit();
            var contentRoot = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(contentRoot) == false)
            {
                Environment.CurrentDirectory = contentRoot;
            }

            // UI进程会显式传入它将要访问的端口。必须在 ConfigureWebHost 之前完成，
            // 否则 GlobalListener.UiHttpPort 已按默认逻辑选好端口，两端会对不上。
            ApplyUiHttpPort(args);

            var options = new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot
            };
            CreateWebApplication(options).Run(singleton: true);
        }

        /// <summary>
        /// 应用UI内部通信端口。
        /// 仅当传入的是合法端口且当前可用时才采用；否则回退到动态分配，
        /// 保证主程序不会因为一个坏参数而起不来。
        /// </summary>
        /// <param name="args"></param>
        private static void ApplyUiHttpPort(string[] args)
        {
            var value = GetArgumentValue(args, "--UiHttpPort");
            if (int.TryParse(value, out var port) == false || port <= 0 || port > 65535)
            {
                return;
            }

            // 端口被占用时不能抢占：UDP日志端口与UI通信端口都复用了同一个基数，
            // 但两者一个是TCP一个是UDP，互不冲突，这里只校验TCP。
            if (GlobalListener.CanListenTcp(port) == false)
            {
                return;
            }

            GlobalListener.SetUiHttpPort(port);
        }

        /// <summary>
        /// 读取 <c>--Key=Value</c> 或 <c>--Key Value</c> 两种形式的参数值
        /// </summary>
        /// <param name="args"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private static string? GetArgumentValue(string[] args, string key)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg[(key.Length + 1)..];
                }

                if (string.Equals(arg, key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        /// <summary>
        /// 创建host
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static WebApplication CreateWebApplication(WebApplicationOptions options)
        {
            var builder = WebApplication.CreateBuilder(options);
            builder.ConfigureHost();
            builder.ConfigureWebHost();
            builder.ConfigureConfiguration();
            builder.ConfigureServices();

            var app = builder.Build();
            app.ConfigureApp();
            return app;
        }

    }
}
