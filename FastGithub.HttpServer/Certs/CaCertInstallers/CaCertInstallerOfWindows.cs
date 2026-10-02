using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace FastGithub.HttpServer.Certs.CaCertInstallers
{
    sealed class CaCertInstallerOfWindows : ICaCertInstaller
    {
        private readonly ILogger<CaCertInstallerOfWindows> logger;

        public CaCertInstallerOfWindows(ILogger<CaCertInstallerOfWindows> logger)
        {
            this.logger = logger;
        }

        /// <summary>
        /// 是否支持
        /// </summary>
        /// <returns></returns>
        public bool IsSupported()
        {
            return OperatingSystem.IsWindows();
        }

        /// <summary>
        /// 安装ca证书
        /// </summary>
        /// <param name="caCertFilePath">证书文件路径</param>
        public void Install(string caCertFilePath)
        {
            try
            {
                this.InstallToStore(caCertFilePath, StoreLocation.LocalMachine);
            }
            catch (Exception)
            {
                // LocalMachine 需要管理员权限。降级装到当前用户：
                // .NET、Chrome/Edge、curl.exe 等都会读 CurrentUser\Root，
                // 比"装不上就放弃、只留一句警告"实用得多。
                try
                {
                    this.InstallToStore(caCertFilePath, StoreLocation.CurrentUser);
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, $"请手动安装CA证书{caCertFilePath}到“将所有的证书都放入下列存储”\\“受信任的根证书颁发机构”");
                }
            }

            // 安装后必须自检。本项目的 CA 装不上时不会有任何报错，
            // 表现只是"某些客户端 HTTPS 全失败"，极难定位（本轮已实际踩到）。
            if (this.IsTrusted(caCertFilePath) == false)
            {
                this.logger.LogError($"CA证书未被信任：{caCertFilePath}，部分依赖系统信任链的客户端将无法正常访问。请以管理员身份重新运行，或手动导入到“受信任的根证书颁发机构”。");
            }
        }

        /// <summary>
        /// 安装到指定的证书存储
        /// </summary>
        /// <param name="caCertFilePath"></param>
        /// <param name="storeLocation"></param>
        private void InstallToStore(string caCertFilePath, StoreLocation storeLocation)
        {
            using var store = new X509Store(StoreName.Root, storeLocation);
            store.Open(OpenFlags.ReadWrite);

            using var caCert = new X509Certificate2(caCertFilePath);
            var subjectName = GetCommonName(caCert.Subject);
            foreach (var item in store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false))
            {
                if (item.Thumbprint != caCert.Thumbprint)
                {
                    store.Remove(item);
                }
            }

            if (store.Certificates.Find(X509FindType.FindByThumbprint, caCert.Thumbprint, true).Count == 0)
            {
                store.Add(caCert);
            }
        }

        /// <summary>
        /// 校验ca证书是否已被信任（LocalMachine 或 CurrentUser 任一即可）
        /// </summary>
        /// <param name="caCertFilePath"></param>
        /// <returns></returns>
        public bool IsTrusted(string caCertFilePath)
        {
            try
            {
                if (File.Exists(caCertFilePath) == false)
                {
                    return false;
                }

                using var caCert = new X509Certificate2(caCertFilePath);
                return IsInStore(caCert.Thumbprint, StoreLocation.LocalMachine)
                    || IsInStore(caCert.Thumbprint, StoreLocation.CurrentUser);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 证书是否存在于指定存储
        /// </summary>
        /// <param name="thumbprint"></param>
        /// <param name="storeLocation"></param>
        /// <returns></returns>
        private static bool IsInStore(string thumbprint, StoreLocation storeLocation)
        {
            try
            {
                using var store = new X509Store(StoreName.Root, storeLocation);
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, true).Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 取主题中的CN值。
        /// 原实现用固定下标 <c>caCert.Subject[3..]</c> 截取，主题格式一旦变化就会截出错误内容，
        /// 使 Find 匹配不到已有证书、旧证书清不掉（可能累积多个同名 CA）。
        /// </summary>
        /// <param name="subject"></param>
        /// <returns></returns>
        private static string GetCommonName(string subject)
        {
            foreach (var part in subject.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[3..].Trim();
                }
            }
            return subject;
        }
    }
}
