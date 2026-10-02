namespace FastGithub.Configuration
{
    /// <summary>
    /// 正向代理运行时状态。
    /// 放在 Configuration 层是因为 HttpServer（写入方）与 PacketIntercept（读取方）
    /// 都只引用本工程，彼此不引用，无法直接共享类型。
    /// </summary>
    public static class HttpProxyRuntimeState
    {
        /// <summary>
        /// http代理实际监听的端口；为 null 表示未成功监听（端口被占用或未启动）。
        /// 降级提示必须据此判断，否则会给出一个指向空端口的错误指引。
        /// </summary>
        public static int? ListenedPort { get; set; }
    }
}
