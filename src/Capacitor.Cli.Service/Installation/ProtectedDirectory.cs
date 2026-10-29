using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Service.Installation;

/// <summary>
/// A directory only SYSTEM and Administrators can write, set explicitly rather than inherited. The
/// service runs from here as SYSTEM, so anything a standard user could replace in it would run as
/// SYSTEM; a directory that existed before the install keeps no ACE it brought with it
/// </summary>
[SupportedOSPlatform("windows")]
static class ProtectedDirectory {
    public static void Ensure(string path) {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));

        var directory = new DirectoryInfo(path);
        if (directory.Exists) {
            directory.SetAccessControl(security);
            return;
        }
        Directory.CreateDirectory(directory.Parent!.FullName);
        directory.Create(security);
    }
}
