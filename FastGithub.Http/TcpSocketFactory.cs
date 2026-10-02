using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.Http
{
    /// <summary>
    /// TCP socket 的统一构造与调优
    /// <para>
    /// 本项目所有出口连接（HTTP/HTTPS 反代、SSH、Git、正向代理）都经过这里，
    /// 保证参数一致，也避免各处漏配。
    /// </para>
    /// </summary>
    public static class TcpSocketFactory
    {
        /// <summary>
        /// 接收/发送缓冲区大小。
        /// 默认 64KB 对跨境大文件（release 附件、git pack）偏小：
        /// 缓冲区满会导致对端停发等待窗口更新，往返延迟被放大成吞吐瓶颈。
        /// </summary>
        private const int BUFFER_SIZE = 256 * 1024;

        /// <summary>
        /// 创建用于出口连接的 socket
        /// </summary>
        /// <param name="addressFamily"></param>
        /// <returns></returns>
        public static Socket Create(System.Net.Sockets.AddressFamily addressFamily)
        {
            var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            Configure(socket);
            return socket;
        }

        /// <summary>
        /// 应用统一的TCP参数
        /// </summary>
        /// <param name="socket"></param>
        public static void Configure(Socket socket)
        {
            // Nagle 会把小包攒够再发。git/SSH 是典型的小包交互流，
            // 攒包会把一次往返变成 200ms 的等待，直接表现为 clone 卡顿。
            socket.NoDelay = true;

            try
            {
                socket.ReceiveBufferSize = BUFFER_SIZE;
                socket.SendBufferSize = BUFFER_SIZE;
            }
            catch (SocketException)
            {
                // 某些平台不允许设置超过上限的缓冲区，忽略即可，不影响连通
            }
        }

        /// <summary>
        /// 连接到指定的IP与端口
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public static ValueTask ConnectAsync(Socket socket, IPAddress address, int port, CancellationToken cancellationToken)
        {
            return socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
        }
    }
}
