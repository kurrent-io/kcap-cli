using System.Runtime.Versioning;
using System.Security.AccessControl;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class OwnerOnlyDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task A_new_directory_admits_only_the_user_and_SYSTEM_and_ignores_its_parent() {
        var path = Tmp.PathTo("state");

        OwnerOnlyDirectory.Ensure(path);

        await Assert.That(WindowsAcl.IsProtected(path)).IsTrue();
        await AssertOwnerOnly(path);
    }

    /// <summary>The Users grant on the existing directory, and its inheritance into the file, are what
    /// make the assertions after <see cref="OwnerOnlyDirectory.Ensure"/> mean anything.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task An_existing_directory_and_what_it_holds_lose_every_other_grant() {
        string dir = Tmp.CreateDir("state");
        WindowsAcl.GrantUsersRead(dir);
        var file = Tmp.CreateFile(["state", "prompts", "prompt.md"], "secret");
        await Assert.That(WindowsAcl.Identities(file)).Contains(WindowsAcl.Users.Value);

        OwnerOnlyDirectory.Ensure(dir);

        await AssertOwnerOnly(dir);
        await AssertOwnerOnly(file);
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task An_existing_directory_is_made_0700() {
        string dir = Tmp.CreateDir("state");
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);

        OwnerOnlyDirectory.Ensure(dir);

        await Assert.That(File.GetUnixFileMode(dir))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    static async Task AssertOwnerOnly(string path) {
        await Assert.That(WindowsAcl.Identities(path)).IsEquivalentTo(WindowsAcl.OwnerOnly);

        foreach (var rule in WindowsAcl.Rules(path)) {
            await Assert.That(rule.AccessControlType).IsEqualTo(AccessControlType.Allow);
            await Assert.That(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)).IsTrue();
        }
    }
}
