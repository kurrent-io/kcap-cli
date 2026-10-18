using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class OwnerOnlyDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task A_new_directory_admits_only_the_user_and_SYSTEM_and_ignores_its_parent() {
        var path = Tmp.PathTo("state");

        OwnerOnlyDirectory.Ensure(path);

        var security = new DirectoryInfo(path).GetAccessControl();
        await Assert.That(security.AreAccessRulesProtected).IsTrue();
        await AssertOwnerOnly(security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)));
    }

    /// <summary>The Users grant on the existing directory, and its inheritance into the file, are what
    /// make the assertions after <see cref="OwnerOnlyDirectory.Ensure"/> mean anything.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task An_existing_directory_and_what_it_holds_lose_every_other_grant() {
        var dir = Tmp.CreateDir("state");
        var info = new DirectoryInfo(dir);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var open = info.GetAccessControl();
        open.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(open);
        var file = Tmp.CreateFile(["state", "prompts", "prompt.md"], "secret");
        await Assert.That(Identities(new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))))
            .Contains(users.Value);

        OwnerOnlyDirectory.Ensure(dir);

        await AssertOwnerOnly(info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)));
        await AssertOwnerOnly(new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)));
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task An_existing_directory_is_made_0700() {
        var dir = Tmp.CreateDir("state");
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);

        OwnerOnlyDirectory.Ensure(dir);

        await Assert.That(File.GetUnixFileMode(dir))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    static async Task AssertOwnerOnly(AuthorizationRuleCollection rules) {
        var expected = new[] { WindowsIdentity.GetCurrent().User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };

        await Assert.That(Identities(rules).Distinct().Order().ToArray()).IsEquivalentTo(expected.Order().ToArray());

        foreach (FileSystemAccessRule rule in rules) {
            await Assert.That(rule.AccessControlType).IsEqualTo(AccessControlType.Allow);
            await Assert.That(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)).IsTrue();
        }
    }

    [SupportedOSPlatform("windows")]
    static List<string> Identities(AuthorizationRuleCollection rules) =>
        rules.Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();
}
