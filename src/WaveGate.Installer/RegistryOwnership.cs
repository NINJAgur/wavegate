using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace WaveGate.Installer;

/// <summary>
/// MMDevices endpoint keys are owned by SYSTEM/TrustedInstaller and are read-only
/// even for elevated admins. We temporarily take ownership, grant Administrators
/// write access, and later put the original security descriptor back.
/// </summary>
internal static class RegistryOwnership
{
    private const AccessControlSections OwnerAndDacl = AccessControlSections.Owner | AccessControlSections.Access;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static string GetSddl(RegistryKey hklm, string path)
    {
        using var key = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.Default, RegistryRights.ReadPermissions)
            ?? throw new InvalidOperationException($"Registry key not found: HKLM\\{path}");
        return key.GetAccessControl(OwnerAndDacl).GetSecurityDescriptorSddlForm(OwnerAndDacl);
    }

    /// <summary>Makes the key writable for Administrators. Returns false if it already was.</summary>
    public static bool EnsureWritable(RegistryKey hklm, string path)
    {
        if (CanWrite(hklm, path)) return false;

        Privileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");

        using (var key = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.Default, RegistryRights.TakeOwnership)!)
        {
            var owner = new RegistrySecurity();
            owner.SetOwner(Administrators);
            key.SetAccessControl(owner);
        }

        using (var key = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.Default,
                   RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!)
        {
            var acl = key.GetAccessControl(OwnerAndDacl);
            acl.AddAccessRule(new RegistryAccessRule(Administrators, RegistryRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            key.SetAccessControl(acl);
        }
        return true;
    }

    public static void Restore(RegistryKey hklm, string path, string sddl)
    {
        // Handing ownership back to TrustedInstaller/SYSTEM requires SeRestorePrivilege.
        Privileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");

        using var key = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.Default,
            RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.ReadPermissions)
            ?? throw new InvalidOperationException($"Registry key not found: HKLM\\{path}");

        var original = new RegistrySecurity();
        original.SetSecurityDescriptorSddlForm(sddl, OwnerAndDacl);
        key.SetAccessControl(original);
    }

    private static bool CanWrite(RegistryKey hklm, string path)
    {
        try
        {
            using var key = hklm.OpenSubKey(path, writable: true);
            return key is not null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static class Privileges
    {
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint TOKEN_QUERY = 0x8;
        private const uint SE_PRIVILEGE_ENABLED = 0x2;
        private const int ERROR_NOT_ALL_ASSIGNED = 1300;

        public static void Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                throw new Win32Exception();
            try
            {
                foreach (var name in names)
                {
                    if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
                    var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                    if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) ||
                        Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                        throw new Win32Exception($"Could not enable {name}; is the process elevated?");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state,
            uint bufferLength, IntPtr previous, IntPtr returnLength);
    }
}
