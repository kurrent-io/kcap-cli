using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core;

/// <summary><c>0700</c> on Unix; on Windows a protected DACL, inherited beneath it, for the current user and
/// SYSTEM. SYSTEM stays, as root does under <c>0700</c>: it can take any file anyway, and backup and
/// antivirus run as it.</summary>
public static class OwnerOnlyDirectory {
    const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> if missing and restricts it. On Windows what inherits from
    /// it inside gets the new DACL too; an explicit grant on an entry survives. Throws on failure.</summary>
    public static void Ensure(string path) {
        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(path).SetAccessControl(OwnerOnlySecurity());
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyMode);
        File.SetUnixFileMode(path, OwnerOnlyMode);
    }

    [SupportedOSPlatform("windows")]
    static DirectorySecurity OwnerOnlySecurity() {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }) {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
