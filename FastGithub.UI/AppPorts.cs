using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace FastGithub.UI
{
    /// <summary>
    /// UI进程与主程序共享的端口。
    /// 主程序按 --UiHttpPort 监听，UI 按同一个端口访问，两端不再各自猜。
    /// </summary>
    static class AppPorts
    {
        /// <summary>
        /// UI内部通信端口的起始值，与主程序 GlobalListener.UiHttpPortBase 保持一致
        /// </summary>
        public const int UiHttpPortBase = 45678;

        /// <summary>
        /// UDP日志端口的起始值，与主程序 AppOptions.UdpLoggerPort 默认值保持一致
        /// </summary>
        public const int UdpLoggerPortBase = 38457;

        /// <summary>
        /// UI内部通信的HTTP基地址
        /// <para>
        /// 【重要】必须用惰性属性，不能写成静态字段初始化。
        /// 原实现把它声明在 <see cref="UiHttpPort"/> 之前并直接插值：
        /// <c>public static string UiHttpBaseUrl { get; } = $"http://127.0.0.1:{UiHttpPort}";</c>
        /// C# 静态字段/属性按声明顺序初始化，插值时 <see cref="UiHttpPort"/> 仍是默认值 0，
        /// 于是 BaseUrl 恒为 "http://127.0.0.1:0"。
        /// 而主程序监听的是真实端口（UI 通过 --UiHttpPort 传入），
        /// 两端端口对不上，UI 的所有内部 HTTP 调用都会抛 HttpRequestException
        /// （已实测复现：BaseUrl 最终值为 http://127.0.0.1:0）。
        /// 症状是"流量图不显示"且"更新IP 按钮无效"，且日志里看不到任何服务端错误，
        /// 极难定位——已改为惰性求值，读取时才计算，与声明顺序完全无关。
        /// </para>
        /// </summary>
        public static string UiHttpBaseUrl => $"http://127.0.0.1:{UiHttpPort}";

        /// <summary>
        /// UI内部通信端口
        /// </summary>
        public static int UiHttpPort { get; } = GetAvailableTcpPort(UiHttpPortBase);

        /// <summary>
        /// 获取可用的TCP端口
        /// </summary>
        /// <param name="minPort"></param>
        /// <returns></returns>
        private static int GetAvailableTcpPort(int minPort)
        {
            var used = new HashSet<int>();
            try
            {
                foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                {
                    used.Add(endpoint.Port);
                }
            }
            catch (Exception)
            {
            }

            for (var port = minPort; port < IPEndPoint.MaxPort; port++)
            {
                if (used.Contains(port) == false)
                {
                    return port;
                }
            }

            throw new InvalidOperationException("当前无可用的端口");
        }
    }
}
