using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
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
        private const int ReadOwnerAndDacl = 0x5;
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

        internal static Status ReadStatus(string executablePath)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = GetUserSid(identity);
            var status = ReadTaskStatus(OpenTaskFolder(), sid, executablePath);
            if (HasLegacyEntry() == false)
            {
                return status;
            }

            return status switch
            {
                Status.Off => Status.LegacyEntry,
                Status.Enabled => Status.EnabledWithLegacyEntry,
                _ => status
            };
        }

        internal static void SetEnabled(bool enabled, string executablePath)
        {
            if (Program.IsAdministrator() == false)
            {
                throw new UnauthorizedAccessException("修改登录自启需要管理员权限。");
            }

            using var identity = WindowsIdentity.GetCurrent();
            var sid = GetUserSid(identity);
            var taskName = TaskPrefix + sid;
            var folder = OpenTaskFolder();

            if (enabled)
            {
                AutoStartInstallPath.Validate(executablePath);
                if (ReadTaskStatus(folder, sid, executablePath) != Status.Enabled)
                {
                    // 任务由计划程序管理，仅 SYSTEM 和已提升的管理员可修改。交互登录无需保存密码。
                    ((dynamic)folder).RegisterTask(taskName, BuildXml(sid, executablePath),
                        CreateOrUpdate | DontAddPrincipalAce, sid, null, InteractiveToken, TaskSecurity);
                    if (ReadTaskStatus(folder, sid, executablePath) != Status.Enabled)
                    {
                        throw new InvalidOperationException("计划任务回读结果与登录自启设置不一致。");
                    }
                }
            }
            else
            {
                DeleteTask(folder, taskName);
            }

            // 启用时先确认新任务。关闭时先撤销高权限入口，再清理旧项。
            if (HasLegacyEntry())
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                runKey?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
            }
        }

        private static bool HasLegacyEntry()
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKey);
            return runKey?.GetValueNames().Contains(LegacyRunValue, StringComparer.OrdinalIgnoreCase) == true;
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

        private static Status ReadTaskStatus(object folder, string sid, string executablePath)
        {
            try
            {
                dynamic task = ((dynamic)folder).GetTask(TaskPrefix + sid);
                if ((bool)task.Enabled == false)
                {
                    return Status.Off;
                }

                return Matches((string)task.Xml, sid, executablePath) &&
                    HasProtectedSecurity((string)task.GetSecurityDescriptor(ReadOwnerAndDacl))
                    ? Status.Enabled : Status.NeedsUpdate;
            }
            catch (Exception ex) when (ex.HResult == TaskNotFound)
            {
                return Status.Off;
            }
        }

        private static bool HasProtectedSecurity(string sddl)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            var acl = descriptor.DiscretionaryAcl;
            if (descriptor.Owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) != true ||
                (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || acl?.Count != 2)
            {
                return false;
            }

            var grantsAdmin = false;
            var grantsSystem = false;
            foreach (var entry in acl)
            {
                if (entry is CommonAce ace && ace.AceQualifier == AceQualifier.AccessAllowed &&
                    ace.AceFlags == AceFlags.None && ace.AccessMask == (int)FileSystemRights.FullControl)
                {
                    grantsAdmin |= ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
                    grantsSystem |= ace.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid);
                }
            }

            return grantsAdmin && grantsSystem;
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
                (string?)settings?.Element(ns + "MultipleInstancesPolicy") == "IgnoreNew" &&
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
