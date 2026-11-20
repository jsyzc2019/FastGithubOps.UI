using Microsoft.Win32;
using System;
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
        internal const string BackendFileName = "fastgithub.exe";
        private const int ElevationRequired = 740;

        [STAThread]
        static void Main(string[] args)
        {
            var scheduledStart = Array.IndexOf(args, AutoStartTask.StartupArgument) >= 0;
            // 清单负责让 Windows 请求提权，实际令牌检查涵盖兼容性设置等启动方式。
            if (IsAdministrator() == false)
            {
                if (scheduledStart == false)
                {
                    MessageBox.Show("FastGithub.UI 需要管理员权限。请右键程序，选择「以管理员身份运行」。",
                        "FastGithub", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                Environment.ExitCode = ElevationRequired;
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            using var mutex = new Mutex(true, MUTEX_NAME, out var isFirstInstance);
            if (isFirstInstance == false)
            {
                return;
            }

            if (StartFastGithub(scheduledStart) == false && scheduledStart)
            {
                Environment.ExitCode = 2; // ERROR_FILE_NOT_FOUND
                return;
            }

            SetWebBrowserDPI();
            SetWebBrowserVersion();

            var app = new Application();
            // 【v2.6.11 借鉴上游 v2.3.2】兜底未处理异常：上游实证过
            // "日志页右键复制时剪贴板被占用 -> COMException -> 托盘进程崩溃
            //  -> 后端随父进程退出 -> 加速静默失效" 的完整故障链。
            // UI 是后端的父进程。普通异常继续记录并处理，内存耗尽由 WPF 默认路径结束进程。
            app.DispatcherUnhandledException += (s, e) =>
            {
                var terminating = IsOutOfMemory(e.Exception);
                e.Handled = terminating == false;
                WriteUiError("DispatcherUnhandledException", e.Exception, terminating);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteUiError("AppDomain.UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);
            app.StartupUri = new Uri(MAIN_WINDOWS, UriKind.Relative);
            app.Run();
        }

        private static bool IsOutOfMemory(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is OutOfMemoryException)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把 UI 未处理异常、进程号和终止状态追加写入 logs/ui-error.log。
        /// <para>落盘本身任何失败都必须被吞掉——它是兜底的一部分，不能反过来让 UI 崩掉。</para>
        /// </summary>
        private static void WriteUiError(string source, Exception? exception, bool terminating)
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                using var process = Process.GetCurrentProcess();
                File.AppendAllText(
                    Path.Combine(dir, "ui-error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] PID={process.Id} Terminating={terminating} {exception}{Environment.NewLine}");
            }
            catch
            {
                // 落盘失败不影响异常兜底
            }
        }

        /// <summary>
        /// 当前进程是否以管理员身份运行（已提升 token）。
        /// <para>UAC 下普通令牌的 Administrators 组仅用于拒绝访问，此判断能区分「已提升」与「普通权限」，
        /// 契合 WinDivert 内核驱动对管理员权限的要求。</para>
        /// </summary>
        public static bool IsAdministrator()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
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
        /// 启动继承当前管理员令牌的后端进程。
        /// </summary>
        private static bool StartFastGithub(bool scheduledStart)
        {
            var fastgithubPath = FindFastGithubPath(allowDevelopmentBuild: scheduledStart == false);
            if (fastgithubPath == null)
            {
                if (scheduledStart == false)
                {
                    MessageBox.Show($"未找到 {BackendFileName}，请确认它与 FastGithub.UI.exe 在同一目录下。",
                        "FastGithub", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fastgithubPath,
                WorkingDirectory = Path.GetDirectoryName(fastgithubPath),
                Arguments = $"--ParentProcessId={Process.GetCurrentProcess().Id} --UdpLoggerPort={UdpLogger.Port} --UiHttpPort={AppPorts.UiHttpPort}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            return process != null;
        }

        /// <summary>
        /// 查找fastgithub.exe路径
        /// </summary>
        /// <returns></returns>
        private static string? FindFastGithubPath(bool allowDevelopmentBuild)
        {
            // 生产环境：与UI同目录
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localPath = Path.Combine(baseDir, BackendFileName);
            if (File.Exists(localPath))
            {
                return localPath;
            }

            if (allowDevelopmentBuild == false)
            {
                return null;
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
                            var devPath = Path.Combine(candidate, BackendFileName);
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
    }
}
