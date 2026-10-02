namespace FastGithub
{
    /// <summary>
    /// app选项
    /// </summary>
    public record AppOptions
    {
        /// <summary>
        /// 父进程id
        /// </summary>
        public int ParentProcessId { get; init; }

        /// <summary>
        /// udp日志服务器端口
        /// </summary>
        public int UdpLoggerPort { get; init; }

        /// <summary>
        /// UI内部通信端口。
        /// 不指定时由 GlobalListener 动态分配；由UI进程启动时显式传入，
        /// 避免端口被占用后主程序静默换端口、导致UI的流量图表与"更新IP"永久失效。
        /// </summary>
        public int UiHttpPort { get; init; }
    }
}
