using System;
using System.Configuration;
using System.Diagnostics;
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

            var version = this.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            this.Title = $"{FASTGITHUB_UI} v{version}";
            this.notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Visible = true,
                Text = FASTGITHUB_UI,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[] { refreshIp, upgrade, settings, exit }),
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
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10d) };
                var response = await httpClient.GetAsync($"{AppPorts.UiHttpBaseUrl}/refresh-ip");
                var message = await response.Content.ReadAsStringAsync();

                // 主程序未运行时给出明确指引，而不是抛一句 IsSuccessStatusCode 的通用错误
                if (response.IsSuccessStatusCode == false)
                {
                    this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, $"IP更新失败：{message}", System.Windows.Forms.ToolTipIcon.Error);
                    return;
                }

                this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, "已发送IP更新请求，正在重新解析", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                this.notifyIcon.ShowBalloonTip(3000, FASTGITHUB_UI, $"IP更新失败：{ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
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
            this.notifyIcon.Icon = null;
            this.notifyIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
