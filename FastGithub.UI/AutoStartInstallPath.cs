using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FastGithub.UI
{
    internal static class AutoStartInstallPath
    {
        private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
        private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
        private const FileSystemRights ChangeExistingPath = GenericWrite | GenericAll |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;
        private const FileSystemRights ChangeInstallation = ChangeExistingPath |
            FileSystemRights.WriteData | FileSystemRights.AppendData;

        private static readonly SecurityIdentifier TrustedInstaller =
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        internal static void Validate(string executablePath)
        {
            var directory = new FileInfo(executablePath).Directory;
            // 任务计划程序会展开百分号变量，动作必须始终指向固定的本地文件。
            if (directory?.Parent == null || executablePath.Contains("%") ||
                new DriveInfo(directory.Root.FullName).DriveType != DriveType.Fixed)
            {
                throw new InvalidOperationException("登录自启需要固定本地磁盘上的受保护安装目录，路径不能含 %。");
            }

            if (File.Exists(Path.Combine(directory.FullName, Program.BackendFileName)) == false)
            {
                throw new InvalidOperationException("登录自启需要 FastGithub.UI.exe 与 fastgithub.exe 位于同一目录。");
            }

            ValidateAncestors(directory.Parent);
            ValidateInstallation(directory);
        }

        private static void ValidateAncestors(DirectoryInfo? directory)
        {
            if (directory != null)
            {
                ValidateAncestors(directory.Parent);
                ValidateAccess(directory, ChangeExistingPath);
            }
        }

        private static void ValidateInstallation(DirectoryInfo directory)
        {
            ValidateAccess(directory, ChangeInstallation);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child)
                {
                    ValidateInstallation(child);
                }
                else
                {
                    ValidateAccess(entry, ChangeInstallation);
                }
            }
        }

        private static void ValidateAccess(FileSystemInfo entry, FileSystemRights prohibitedRights)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"登录自启路径不能经过目录联接或符号链接。\n{entry.FullName}");
            }

            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            var security = entry is DirectoryInfo directory
                ? (FileSystemSecurity)directory.GetAccessControl(sections)
                : ((FileInfo)entry).GetAccessControl(sections);
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            var acl = descriptor.DiscretionaryAcl;
            if (acl == null || IsTrusted(descriptor.Owner) == false || AllowsModification(acl, prohibitedRights))
            {
                throw new InvalidOperationException(
                    $"普通账户可以更改此安装路径或文件。请以管理员身份将完整程序解压到受保护目录（例如 Program Files）。\n{entry.FullName}");
            }
        }

        private static bool AllowsModification(RawAcl acl, FileSystemRights prohibitedRights)
        {
            // 检查允许 ACE，包括以通用访问掩码表达的写权限；无法确认安全的 ACL 由管理员调整。
            foreach (GenericAce entry in acl)
            {
                if (entry is QualifiedAce rule && rule.AceQualifier == AceQualifier.AccessAllowed &&
                    (rule.AceFlags & AceFlags.InheritOnly) == 0 &&
                    (rule.AccessMask & (int)prohibitedRights) != 0 && IsTrusted(rule.SecurityIdentifier) == false)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTrusted(SecurityIdentifier? sid)
            => sid != null && (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.Equals(TrustedInstaller));
    }
}
