using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace FastGithub.UI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly System.Windows.Forms.NotifyIcon notifyIcon;
        private readonly System.Windows.Forms.MenuItem pickIp;
        private readonly System.Windows.Forms.MenuItem restoreIp;
        private bool closed;
        private const string FASTGITHUB_UI = "FastGithub.UI";
        private const string RELEASES_URI = "https://github.com/yuan71058/FastGithub/releases";

        public MainWindow()
        {
            InitializeComponent();

            var upgrade = new System.Windows.Forms.MenuItem("检测更新(&U)");
            upgrade.Click += (s, e) => Process.Start(RELEASES_URI);

            var refreshIp = new System.Windows.Forms.MenuItem("更新IP(&R)");
            refreshIp.Click += async (s, e) => await this.RefreshIpAsync();

            var settings = new System.Windows.Forms.MenuItem("设置(&S)");
            settings.Click += (s, e) =>
            {
                var settingsWindow = new SettingsWindow();
                settingsWindow.Owner = this.IsVisible ? this : null;
                settingsWindow.ShowDialog();
            };

            var exit = new System.Windows.Forms.MenuItem("关闭应用(&C)");
            exit.Click += (s, e) => this.ExitAppAsync();

            // GitHub 选路配置和最近一次实际建连状态。
            this.pickIp = new System.Windows.Forms.MenuItem("重新选优IP(&P)");
            this.pickIp.Click += async (s, e) => await this.UpdateGitHubIpAsync(restoreAutomatic: false);
            this.restoreIp = new System.Windows.Forms.MenuItem("恢复GitHub自动选路(&A)");
            this.restoreIp.Click += async (s, e) => await this.UpdateGitHubIpAsync(restoreAutomatic: true);

            var ipStatus = new System.Windows.Forms.MenuItem("查看GitHub建连状态(&I)");
            ipStatus.Click += async (s, e) =>
            {
                ipStatus.Enabled = false;
                try
                {
                    await this.RequestGitHubIpAsync(HttpMethod.Get, "/github-ip-status", TimeSpan.FromSeconds(5d), showDialog: true);
                }
                finally
                {
                    if (this.closed == false)
                    {
                        ipStatus.Enabled = true;
                    }
                }
            };

            var openConfig = new System.Windows.Forms.MenuItem("打开IP配置(&O)");
            openConfig.Click += (s, e) => this.OpenIpConfigFile();

            var openDir = new System.Windows.Forms.MenuItem("打开工具目录(&D)");
            openDir.Click += (s, e) => this.OpenToolDirectory();

            var separator = new System.Windows.Forms.MenuItem("-");

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            this.Title = $"{FASTGITHUB_UI} v{version}";
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = FASTGITHUB_UI,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[]
                    { refreshIp, this.pickIp, this.restoreIp, ipStatus, openConfig, openDir, separator, upgrade, settings, exit }),
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath)
            };

            this.notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.Activate();
                    this.WindowState = WindowState.Normal;
                }
            };

            if (Properties.Settings.Default.StartMinimized)
            {
                this.Hide();
            }
        }

        /// <summary>
        /// 请求主程序更新固定 IP 或恢复自动选路。
        /// </summary>
        private async Task UpdateGitHubIpAsync(bool restoreAutomatic)
        {
            this.pickIp.Enabled = false;
            this.restoreIp.Enabled = false;
            try
            {
                if (restoreAutomatic == false)
                {
                    this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, "正在探测 GitHub IP，完成后会显示结果。", System.Windows.Forms.ToolTipIcon.Info);
                }
                var path = restoreAutomatic ? "/restore-github-ip" : "/pick-github-ip";
                var timeout = restoreAutomatic ? TimeSpan.FromSeconds(10d) : TimeSpan.FromMinutes(5d);
                await this.RequestGitHubIpAsync(HttpMethod.Post, path, timeout);
            }
            finally
            {
                if (this.closed == false)
                {
                    this.pickIp.Enabled = true;
                    this.restoreIp.Enabled = true;
                }
            }
        }

        private async Task RequestGitHubIpAsync(HttpMethod method, string path, TimeSpan timeout, bool showDialog = false)
        {
            bool success;
            string message;
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
                {
                    Timeout = timeout
                };
                using var request = new HttpRequestMessage(method, $"{AppPorts.UiHttpBaseUrl}{path}");
                request.Headers.Add("X-FastGithub-UI", "1");
                using var response = await client.SendAsync(request);
                success = response.IsSuccessStatusCode;
                message = response.Content == null ? string.Empty : await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = $"主程序返回 HTTP {(int)response.StatusCode}，请确认 UI 和主程序使用同一版本。";
                }
            }
            catch (TaskCanceledException)
            {
                success = false;
                message = "GitHub IP 请求超时，请查看主程序日志。";
            }
            catch (Exception ex)
            {
                success = false;
                message = $"GitHub IP 请求失败：{ex.Message}";
            }

            if (this.closed)
            {
                return;
            }

            if (showDialog)
            {
                System.Windows.MessageBox.Show(message, "GitHub 建连状态", MessageBoxButton.OK,
                    success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            else
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, message,
                    success ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        /// <summary>
        /// 用记事本打开 IP 配置文件（appsettings/appsettings.github.json）。
        /// </summary>
        private void OpenIpConfigFile()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings", "appsettings.github.json");
            if (File.Exists(configPath) == false)
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, "未找到 appsettings/appsettings.github.json", System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{configPath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, $"打开配置失败：{ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// 在资源管理器中打开 FastGitHub 程序目录。
        /// </summary>
        private void OpenToolDirectory()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDomain.CurrentDomain.BaseDirectory}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, $"打开目录失败：{ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// 退出应用：先确认、再通知主程序停机、最后关自己
        /// </summary>
        /// <remarks>
        /// 【v2.6.4】原实现只有 <c>this.Close()</c>，存在三个问题：
        ///   1) 关闭窗口的 <c>WndProc</c> 钩子把 SC_CLOSE 拦下来改成了 <c>Hide()</c>，
        ///      所以"点 X"根本不是退出，菜单「关闭应用」走 Close 时才真正触发 ——
        ///      同一个动作有两种语义，用户完全无法预期；
        ///   2) 没有任何确认，而主程序一旦退出，GitHub 立刻回到未加速状态，
        ///      误点一次就会明显感觉"怎么又变慢了"；
        ///   3) 只关自己而不通知主程序，主程序会变成孤儿进程继续用 WinDivert 拦截流量。
        /// 现在统一为：确认 → 通知主程序 /shutdown → 关自己。
        /// </remarks>
        private async void ExitAppAsync()
        {
            var answer = System.Windows.MessageBox.Show(
                "确定要关闭 FastGithub 吗？\n关闭后 GitHub 将恢复为直连（不再加速）。",
                FASTGITHUB_UI,
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Cancel);

            if (answer != System.Windows.MessageBoxResult.OK)
            {
                return;
            }

            await this.NotifyShutdownAsync();

            // 无论通知是否成功都要关掉自己：主程序还有父进程轮询兜底，
            // 这里的目的是尽最大努力保证"关闭后不留孤儿进程"。
            this.Close();
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 通知主程序主动停机（新增的 /shutdown 端点，仅监听在 localhost 内部端口）
        /// </summary>
        /// <returns>通知是否成功送达</returns>
        private async Task<bool> NotifyShutdownAsync()
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3d) };
                using var response = await httpClient.GetAsync($"{AppPorts.UiHttpBaseUrl}/shutdown");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                // 主程序可能已经不在了，这不算失败——父进程轮询会兜住。
                // 只在日志/提示层面留痕，不阻断退出流程。
                System.Diagnostics.Debug.WriteLine($"通知主程序停机失败（主程序可能已退出）：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 通过UI内部通信端口请求fastgithub刷新IP
        /// </summary>
        /// <returns></returns>
        private async Task RefreshIpAsync()
        {
            try
            {
                // 【v2.6.11 借鉴上游 v2.3.2】两处修正：
                // 1) 超时 10s -> 60s：后端刷新是"清缓存→拉hosts源→全量重测速"，
                //    实测量级在十几秒；10s 会在刷新中途断开请求，
                //    而后端已把请求绑定到刷新动作（旧版），等于把刷新拦腰砍断，
                //    系统停在"缓存已清、新结果未就绪"的半刷新态。
                //    （后端 v2.6.11 起已与请求生命周期解耦，这里是双保险。）
                // 2) 后端返回 JSON 真实结果（域名数/可用IP数/hosts源状态），
                //    弹泡直接展示结果，而不是永远"已发送请求，正在重新解析"。
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60d) };
                var requestTask = httpClient.GetStringAsync($"{AppPorts.UiHttpBaseUrl}/refresh-ip");

                // 3 秒未完成先给一次反馈，避免点击后长时间没有任何提示
                var completedTask = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(3d)));
                if (completedTask != requestTask)
                {
                    this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, "IP更新进行中，正在重新解析与测速…", System.Windows.Forms.ToolTipIcon.Info);
                }

                var json = await requestTask;

                RefreshIpResult? result = null;
                try
                {
                    result = Newtonsoft.Json.JsonConvert.DeserializeObject<RefreshIpResult>(json);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // 后端为旧版本时返回纯文本，按"未知结果"处理
                }

                string message;
                if (result != null && result.TimedOut)
                {
                    message = "IP更新部分完成（45秒预算耗尽，后台继续刷新中）";
                }
                else if (result != null)
                {
                    message = $"IP更新完成：{result.DomainCount}个域名、{result.AddressCount}个可用IP";
                    message += result.HostsUpdated
                        ? $"（在线hosts源{result.HostsDomainCount}个域名）"
                        : $"（在线hosts源更新失败：{result.HostsError}）";
                }
                else
                {
                    message = "已触发IP更新，但后端未返回刷新结果（后端可能为旧版本）";
                }

                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, message, System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                this.notifyIcon.ShowBalloonTip(5000, FASTGITHUB_UI, $"IP更新失败：{ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// 拦截最小化与关闭事件
        /// </summary>
        /// <param name="e"></param>
        /// <remarks>
        /// 【v2.6.4】原实现把 <c>SC_MINIMIZE</c> 与 <c>SC_CLOSE</c> 一律改成 <c>Hide()</c>，
        /// 于是"点 X"和"点最小化"效果完全相同，且没有任何提示——
        /// 用户很容易以为应用已关闭，实际上主程序仍在拦截网络。
        /// 现在区分两者：最小化静默隐藏，点 X 则隐藏到托盘并提示"要退出请用托盘菜单"，
        /// 把"隐藏"与"退出"的语义差异明确告知用户。
        /// 真正的退出入口统一为托盘菜单「关闭应用」（带确认）。
        /// </remarks>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
            hwndSource.AddHook(WndProc);

            IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_SYSCOMMAND = 0x112;
                const int SC_MINIMIZE = 0xf020;
                const int SC_CLOSE = 0xf060;

                if (msg == WM_SYSCOMMAND)
                {
                    var command = wParam.ToInt32();
                    if (command == SC_MINIMIZE)
                    {
                        this.Hide();
                        handled = true;
                    }
                    else if (command == SC_CLOSE)
                    {
                        this.Hide();

                        // 只在窗口原本是可见状态时才提示，避免每次点 X 都弹一次。
                        if (this.notifyIcon.Visible)
                        {
                            this.notifyIcon.ShowBalloonTip(
                                3000,
                                FASTGITHUB_UI,
                                "已隐藏到托盘，GitHub 仍在加速。要退出请右键托盘图标并选择「关闭应用」。",
                                System.Windows.Forms.ToolTipIcon.Info);
                        }

                        handled = true;
                    }
                }
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 关闭时
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClosed(EventArgs e)
        {
            this.closed = true;
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
