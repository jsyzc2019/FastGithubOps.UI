using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;

namespace FastGithub.UI
{
    public class UdpLog
    {
        public DateTime Timestamp { get; set; }

        public LogLevel Level { get; set; }

        public string Message { get; set; } = string.Empty;

        public string SourceContext { get; set; } = string.Empty;

        public string Color => this.Level <= LogLevel.Information ? "#333" : "IndianRed";

        /// <summary>
        /// 复制到剪贴板
        /// </summary>
        /// <remarks>
        /// 【v2.6.11 借鉴上游 v2.3.2】剪贴板可能被其它进程占用，
        /// 此时 SetText 会抛 COMException 且曾导致托盘进程崩溃（UI 崩溃后端随之退出）。
        /// 这里重试 3 次（间隔 100ms）并吞掉最终失败——复制失败不值得让应用死掉。
        /// </remarks>
        public void SetToClipboard()
        {
            var text = $"{this.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\r\n{this.Message}";
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

    }

}
