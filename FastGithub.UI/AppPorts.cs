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
        /// </summary>
        public static string UiHttpBaseUrl { get; } = $"http://127.0.0.1:{UiHttpPort}";

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
