using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Xml.Linq;

namespace FastGithub.UI
{
    internal static class AutoStartTask
    {
        internal const string StartupArgument = "--scheduled-start";
        private const string TaskPrefix = "FastGithub.UI-";
        private const string LegacyRunValue = "FastGithub.UI";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const int TaskNotFound = unchecked((int)0x80070002);
        private const int CreateOrUpdate = 0x6;
        private const int DontAddPrincipalAce = 0x10;
        private const int InteractiveToken = 3;
        private const string TaskSecurity = "O:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";
        private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        internal enum Status
        {
            Off,
            LegacyEntry,
            Enabled,
            EnabledWithLegacyEntry,
            NeedsUpdate
        }

        internal static Status ReadStatus()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = GetUserSid(identity);
            var folder = OpenTaskFolder();
            var xml = ReadEnabledTaskXml(folder, TaskPrefix + sid);
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKey);
            var hasLegacyEntry = runKey?.GetValue(LegacyRunValue) != null;

            if (xml == null)
            {
                return hasLegacyEntry ? Status.LegacyEntry : Status.Off;
            }

            if (Matches(xml, sid, System.Windows.Forms.Application.ExecutablePath) == false)
            {
                return Status.NeedsUpdate;
            }

            return hasLegacyEntry ? Status.EnabledWithLegacyEntry : Status.Enabled;
        }

        internal static void SetEnabled(bool enabled)
        {
            if (Program.IsAdministrator() == false)
            {
                throw new UnauthorizedAccessException("修改登录自启需要管理员权限。");
            }

            using var identity = WindowsIdentity.GetCurrent();
            var sid = GetUserSid(identity);
            var taskName = TaskPrefix + sid;
            var folder = OpenTaskFolder();
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

            if (enabled)
            {
                var executablePath = System.Windows.Forms.Application.ExecutablePath;
                AutoStartInstallPath.Validate(executablePath);
                var xml = ReadEnabledTaskXml(folder, taskName);
                if (xml == null || Matches(xml, sid, executablePath) == false)
                {
                    // 任务由计划程序管理，仅 SYSTEM 和已提升的管理员可修改。交互登录无需保存密码。
                    ((dynamic)folder).RegisterTask(taskName, BuildXml(sid, executablePath),
                        CreateOrUpdate | DontAddPrincipalAce, sid, null, InteractiveToken, TaskSecurity);
                    xml = ReadEnabledTaskXml(folder, taskName);
                    if (xml == null || Matches(xml, sid, executablePath) == false)
                    {
                        throw new InvalidOperationException("计划任务回读结果与登录自启设置不一致。");
                    }
                }

                // 新任务生效后才删除旧入口，注册失败时保留已有设置。
                runKey?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            }
            else
            {
                runKey?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
                DeleteTask(folder, taskName);
            }
        }

        private static string GetUserSid(WindowsIdentity identity)
            => identity.User?.Value ?? throw new InvalidOperationException("无法获取当前 Windows 账户。");

        private static object OpenTaskFolder()
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Windows 任务计划程序不可用。");
            dynamic scheduler = Activator.CreateInstance(serviceType);
            scheduler.Connect();
            return scheduler.GetFolder(@"\");
        }

        private static string? ReadEnabledTaskXml(object folder, string taskName)
        {
            try
            {
                dynamic task = ((dynamic)folder).GetTask(taskName);
                return (bool)task.Enabled ? (string)task.Xml : null;
            }
            catch (Exception ex) when (ex.HResult == TaskNotFound)
            {
                return null;
            }
        }

        private static void DeleteTask(object folder, string taskName)
        {
            try
            {
                ((dynamic)folder).DeleteTask(taskName, 0);
            }
            catch (Exception ex) when (ex.HResult == TaskNotFound)
            {
                // 任务此前已删除，保持关闭。
            }
        }

        private static string BuildXml(string sid, string executablePath)
        {
            var ns = TaskNamespace;
            var task = new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "在当前管理员账户登录时启动 FastGithub.UI。")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", sid))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XElement(ns + "UserId", sid),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "HighestAvailable"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", false),
                    new XElement(ns + "StopIfGoingOnBatteries", false),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(ns + "Actions",
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", executablePath),
                        new XElement(ns + "Arguments", StartupArgument),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executablePath)))));
            return task.ToString(SaveOptions.DisableFormatting);
        }

        private static bool Matches(string xml, string sid, string executablePath)
        {
            var task = XElement.Parse(xml);
            var ns = TaskNamespace;
            var principal = GetOnlyChild(task.Element(ns + "Principals"), ns + "Principal");
            var trigger = GetOnlyChild(task.Element(ns + "Triggers"), ns + "LogonTrigger");
            var action = GetOnlyChild(task.Element(ns + "Actions"), ns + "Exec");
            var settings = task.Element(ns + "Settings");

            return (string?)principal?.Element(ns + "UserId") == sid &&
                (string?)principal?.Element(ns + "LogonType") == "InteractiveToken" &&
                (string?)principal?.Element(ns + "RunLevel") == "HighestAvailable" &&
                (string?)trigger?.Element(ns + "UserId") == sid &&
                (bool?)trigger?.Element(ns + "Enabled") != false &&
                trigger?.Element(ns + "StartBoundary") == null &&
                trigger?.Element(ns + "EndBoundary") == null &&
                string.Equals((string?)action?.Element(ns + "Command"), executablePath, StringComparison.OrdinalIgnoreCase) &&
                (string?)action?.Element(ns + "Arguments") == StartupArgument &&
                string.Equals((string?)action?.Element(ns + "WorkingDirectory"), Path.GetDirectoryName(executablePath), StringComparison.OrdinalIgnoreCase) &&
                (bool?)settings?.Element(ns + "DisallowStartIfOnBatteries") == false &&
                (bool?)settings?.Element(ns + "StopIfGoingOnBatteries") == false &&
                (bool?)settings?.Element(ns + "RunOnlyIfIdle") != true &&
                (bool?)settings?.Element(ns + "RunOnlyIfNetworkAvailable") != true &&
                (string?)settings?.Element(ns + "ExecutionTimeLimit") == "PT0S";
        }

        private static XElement? GetOnlyChild(XElement? parent, XName name)
        {
            var children = parent?.Elements().Take(2).ToArray();
            return children?.Length == 1 && children[0].Name == name ? children[0] : null;
        }
    }
}
