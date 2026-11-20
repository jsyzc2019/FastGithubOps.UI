using System;
using System.Windows;

namespace FastGithub.UI
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            this.RefreshAutoStartStatus();
            StartMinimizedCheckBox.IsChecked = Properties.Settings.Default.StartMinimized;
        }

        private void RefreshAutoStartStatus()
        {
            try
            {
                var status = AutoStartTask.ReadStatus();
                AutoStartCheckBox.IsEnabled = true;
                AutoStartCheckBox.IsChecked = status == AutoStartTask.Status.Enabled ||
                    status == AutoStartTask.Status.EnabledWithLegacyEntry || status == AutoStartTask.Status.NeedsUpdate;
                AutoStartHint.Text = status switch
                {
                    AutoStartTask.Status.LegacyEntry => "检测到旧版自启项。勾选并保存可迁移，取消勾选并保存可移除旧项。",
                    AutoStartTask.Status.EnabledWithLegacyEntry => "管理员自启已启用。保存时会清理残留的旧版自启项。",
                    AutoStartTask.Status.Enabled => "管理员自启已启用。",
                    AutoStartTask.Status.NeedsUpdate => "自启任务配置与当前安装不同。保持勾选并保存可更新，取消勾选可关闭。",
                    _ => "启用后，当前管理员账户登录 Windows 时将自动启动。"
                };
            }
            catch (Exception ex)
            {
                AutoStartCheckBox.IsChecked = null;
                AutoStartCheckBox.IsEnabled = false;
                AutoStartHint.Text = $"无法读取登录自启任务。{ex.Message}";
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AutoStartCheckBox.IsEnabled)
                {
                    AutoStartTask.SetEnabled(AutoStartCheckBox.IsChecked == true);
                }

                Properties.Settings.Default.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                this.RefreshAutoStartStatus();
                MessageBox.Show(this, ex.Message, "设置保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            this.Close();
        }
    }
}
