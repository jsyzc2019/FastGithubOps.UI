namespace FastGithub.HttpServer.Certs
{
    /// <summary>
    /// CA证书安装器
    /// </summary>
    public interface ICaCertInstaller
    {
        /// <summary>
        /// 是否支持
        /// </summary>
        /// <returns></returns>
        bool IsSupported();

        /// <summary>
        /// 安装ca证书
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        void Install(string caCertFilePath);

        /// <summary>
        /// 校验ca证书是否已被信任。
        /// Install 静默失败是本项目最容易踩的坑：证书没进信任库时，
        /// 所有走系统信任链的客户端（Python、PowerShell、Schannel curl）都会
        /// TLS 失败，而现象只是"网络不通"，极难定位。必须能主动查出来。
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        /// <returns></returns>
        bool IsTrusted(string caCertFilePath);
    }
}
