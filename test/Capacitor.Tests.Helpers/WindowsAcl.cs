using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Tests.Helpers;

/// <summary>Reads a Windows ACL back as the SIDs it admits, explicit and inherited alike.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl {
    public static SecurityIdentifier Users { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>The current user and SYSTEM, sorted the way <see cref="Identities"/> returns them.</summary>
    public static string[] OwnerOnly { get; } = new[] {
        WindowsIdentity.GetCurrent().User!.Value,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
    }.Order().ToArray();

    public static string[] Identities(string path) =>
        Rules(path).Select(r => r.IdentityReference.Value).Distinct().Order().ToArray();

    public static IEnumerable<FileSystemAccessRule> Rules(string path) =>
        Security(path).GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();

    public static bool IsProtected(string path) => Security(path).AreAccessRulesProtected;

    /// <summary>Adds an inheritable read grant for every local user, so what is created beneath
    /// <paramref name="directory"/> starts out readable by other accounts.</summary>
    public static void GrantUsersRead(string directory) {
        var info     = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Adds an explicit read grant for every local user on one file.</summary>
    public static void GrantUsersReadOnFile(string file) {
        var info     = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    static FileSystemSecurity Security(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
}
