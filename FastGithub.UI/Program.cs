using Microsoft.Win32;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace FastGithub.UI
{
    class Program
    {
        private const string MUTEX_NAME = "Global\\FastGithub.UI";
        private const string MAIN_WINDOWS = "MainWindow.xaml";
        private const string FASTGITHUB_PATH = "fastgithub.exe";

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            using var mutex = new Mutex(true, MUTEX_NAME, out var isFirstInstance);
            if (isFirstInstance == false)
            {
                return;
            }

            var settings = Properties.Settings.Default;
            if (settings.AutoStart)
            {
                SetAutoStart(true);
            }
            else
            {
                SetAutoStart(false);
            }

            StartFastGithub();
            SetWebBrowserDPI();
            SetWebBrowserVersion();

            var app = new Application();
            // 【v2.6.11 借鉴上游 v2.3.2】兜底未处理异常：上游实证过
            // "日志页右键复制时剪贴板被占用 -> COMException -> 托盘进程崩溃
            //  -> 后端随父进程退出 -> 加速静默失效" 的完整故障链。
            // UI 是后端的父进程，UI 活着才有加速；未处理异常一律吞掉继续跑。
            // 【v2.6.13 B2，借鉴上游 v2.5.0】兜底同时落盘：
            // 纯吞异常会让"双击没反应 / 界面卡住"完全无线索，落盘后至少有一条可查记录。
            app.DispatcherUnhandledException += (s, e) =>
            {
                WriteUiError("DispatcherUnhandledException", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteUiError("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            app.StartupUri = new Uri(MAIN_WINDOWS, UriKind.Relative);
            app.Run();
        }

        /// <summary>
        /// 把 UI 未处理异常追加写入 logs/ui-error.log。
        /// <para>落盘本身任何失败都必须被吞掉——它是兜底的一部分，不能反过来让 UI 崩掉。</para>
        /// </summary>
        private static void WriteUiError(string source, Exception? exception)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "ui-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {exception}{Environment.NewLine}");
            }
            catch
            {
                // 落盘失败不影响异常兜底
            }
        }

        /// <summary>
        /// 程序集加载失败时
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources"))
            {
                return default;
            }

            var stream = Application.GetResourceStream(new Uri($"Resource/{name}.dll", UriKind.Relative)).Stream;
            var buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            return Assembly.Load(buffer);
        }

        /// <summary>
        /// 设置浏览器版本
        /// </summary>
        private static void SetWebBrowserVersion()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION";
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            using var webBrowser = new System.Windows.Forms.WebBrowser();
            var value = int.Parse($"{webBrowser.Version.Major}000");
            registryKey.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 设置浏览器DPI
        /// </summary>
        private static void SetWebBrowserDPI()
        {
            const string subKey = @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_96DPI_PIXEL";
            var registryKey = Registry.CurrentUser.OpenSubKey(subKey, true);
            if (registryKey == null)
            {
                registryKey = Registry.CurrentUser.CreateSubKey(subKey);
            }
            var name = $"{Process.GetCurrentProcess().ProcessName}.exe";
            registryKey.SetValue(name, 1, RegistryValueKind.DWord);
        }

        /// <summary>
        /// 启动fastgithub
        /// </summary>
        /// <returns></returns>
        private static void StartFastGithub()
        {
            var fastgithubPath = FindFastGithubPath();
            if (fastgithubPath == null)
            {
                // 原实现静默返回，用户看到的现象是"双击没反应"，无法判断是缺文件还是崩溃。
                System.Windows.MessageBox.Show(
                    $"未找到 {FASTGITHUB_PATH}，请确认它与 FastGithub.UI.exe 在同一目录下。",
                    "FastGithub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fastgithubPath,
                Arguments = $"--ParentProcessId={Process.GetCurrentProcess().Id} --UdpLoggerPort={UdpLogger.Port} --UiHttpPort={AppPorts.UiHttpPort}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(startInfo);
        }

        /// <summary>
        /// 查找fastgithub.exe路径
        /// </summary>
        /// <returns></returns>
        private static string? FindFastGithubPath()
        {
            // 生产环境：与UI同目录
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(baseDir, FASTGITHUB_PATH);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            // 开发环境：向上查找FastGithub项目输出目录。
            // 目标框架随 Directory.Build.props 走，这里枚举 net* 目录而不是写死版本，
            // 避免框架升级后这条路径再次失效。
            var searchDir = baseDir;
            for (var i = 0; i < 10; i++)
            {
                searchDir = Path.GetDirectoryName(searchDir);
                if (searchDir == null)
                {
                    break;
                }

                var projectBin = Path.Combine(searchDir, "FastGithub", "bin");
                if (Directory.Exists(projectBin) == false)
                {
                    continue;
                }

                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var configurationDir = Path.Combine(projectBin, configuration);
                    if (Directory.Exists(configurationDir) == false)
                    {
                        continue;
                    }

                    foreach (var frameworkDir in Directory.GetDirectories(configurationDir, "net*"))
                    {
                        foreach (var candidate in Directory.GetDirectories(frameworkDir))
                        {
                            var devPath = Path.Combine(candidate, FASTGITHUB_PATH);
                            if (File.Exists(devPath))
                            {
                                return devPath;
                            }
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 设置开机自启
        /// </summary>
        /// <param name="enable"></param>
        public static void SetAutoStart(bool enable)
        {
            const string appName = "FastGithub.UI";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key == null)
            {
                return;
            }

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    key.SetValue(appName, exePath);
                }
            }
            else
            {
                key.DeleteValue(appName, false);
            }
        }
    }
}
