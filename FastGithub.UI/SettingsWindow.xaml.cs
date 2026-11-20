using System;
using System.Windows;

namespace FastGithub.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly string executablePath = System.Windows.Forms.Application.ExecutablePath;

        public SettingsWindow()
        {
            InitializeComponent();
            this.RefreshAutoStartStatus();
            StartMinimizedCheckBox.IsChecked = Properties.Settings.Default.StartMinimized;
        }

        private void RefreshAutoStartStatus()
        {
            EnableAutoStartButton.Content = "启用自启";
            try
            {
                var status = AutoStartTask.ReadStatus(this.executablePath);
                EnableAutoStartButton.IsEnabled = status != AutoStartTask.Status.Enabled;
                DisableAutoStartButton.IsEnabled = status != AutoStartTask.Status.Off;
                EnableAutoStartButton.Content = status switch
                {
                    AutoStartTask.Status.LegacyEntry => "迁移旧版自启",
                    AutoStartTask.Status.EnabledWithLegacyEntry => "清理旧版自启项",
                    AutoStartTask.Status.NeedsUpdate => "更新自启任务",
                    _ => "启用自启"
                };
                AutoStartHint.Text = status switch
                {
                    AutoStartTask.Status.LegacyEntry => "检测到旧版自启项，可执行迁移或关闭自启。",
                    AutoStartTask.Status.EnabledWithLegacyEntry => "管理员自启已启用，仍有残留的旧版自启项。",
                    AutoStartTask.Status.Enabled => "管理员自启已启用。",
                    AutoStartTask.Status.NeedsUpdate => "现有任务的安装路径、权限或配置需要更新。",
                    _ => "启用后，当前管理员账户登录 Windows 时将自动启动。"
                };
            }
            catch (Exception ex)
            {
                EnableAutoStartButton.IsEnabled = false;
                DisableAutoStartButton.IsEnabled = false;
                AutoStartHint.Text = $"无法读取登录自启任务。{ex.Message}";
            }
        }

        private void EnableAutoStartButton_Click(object sender, RoutedEventArgs e) => this.ApplyAutoStart(true);

        private void DisableAutoStartButton_Click(object sender, RoutedEventArgs e) => this.ApplyAutoStart(false);

        private void ApplyAutoStart(bool enabled)
        {
            try
            {
                AutoStartTask.SetEnabled(enabled, this.executablePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "登录自启修改失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                this.RefreshAutoStartStatus();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Properties.Settings.Default.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "设置保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            this.Close();
        }
    }
}
