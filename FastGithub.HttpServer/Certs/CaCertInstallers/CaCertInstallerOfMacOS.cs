using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace FastGithub.HttpServer.Certs.CaCertInstallers
{
    sealed class CaCertInstallerOfMacOS : ICaCertInstaller
    {
        private readonly ILogger<CaCertInstallerOfMacOS> logger;

        public CaCertInstallerOfMacOS(ILogger<CaCertInstallerOfMacOS> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// 是否支持
        /// </summary>
        /// <returns></returns>
        public bool IsSupported()
        {
            return OperatingSystem.IsMacOS();
        }

        /// <summary>
        /// 安装ca证书
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        public void Install(string caCertFilePath)
        {
            logger.LogWarning($"请手动安装CA证书然后设置信任CA证书{caCertFilePath}");
        }

        /// <summary>
        /// 校验ca证书是否已被加入系统信任（读取 security 命令的退出码）
        /// </summary>
        /// <param name="caCertFilePath"></param>
        /// <returns></returns>
        public bool IsTrusted(string caCertFilePath)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "security",
                    Arguments = $"verify-cert -c \"{caCertFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });

                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
