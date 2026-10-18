using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core;

/// <summary>A directory only the current user can enter: <c>0700</c> on Unix; on Windows a protected
/// DACL granting full control to the current user and SYSTEM, inherited by everything beneath it.
/// Without it a Windows directory inherits its parent's ACL, which a relocated <c>KCAP_DAEMONS_DIR</c>
/// or <c>KCAP_CONFIG_DIR</c> makes whatever that location grants.
///
/// <para>SYSTEM keeps access because it is the OS itself — it can take any file regardless — and
/// removing it breaks backup and antivirus scanning for no confidentiality gain, as <c>0700</c> does
/// not exclude root. Administrators get no grant: one must take ownership first, which is audited.</para></summary>
public static class OwnerOnlyDirectory {
    const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> if it is missing and restricts it, an existing one
    /// included. On Windows the new DACL also replaces what the entries already inside inherited.
    /// Throws when the restriction cannot be applied.</summary>
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
