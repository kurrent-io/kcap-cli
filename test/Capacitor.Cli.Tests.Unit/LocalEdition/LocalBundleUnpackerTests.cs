using TUnit.Core.Enums;
using System.Formats.Tar;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using static Capacitor.Cli.Tests.Unit.LocalEdition.TestBundles;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalBundleUnpackerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Archive => Tmp.PathTo("bundle.tar.gz");
    string Dest    => Tmp.PathTo("out");

    Task Unpack() => LocalBundleUnpacker.UnpackAsync(Archive, Dest, CancellationToken.None);

    async Task AssertRefused(string entryFragment) {
        var ex = await Assert.That(Unpack).Throws<LocalBundleException>();
        await Assert.That(ex!.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
        await Assert.That(ex.Message).Contains(entryFragment);
    }

    static (string Name, TarEntryType Type, string? Content, string? LinkName) Server => RegularFile("./kcap-server", "#!/bin/sh\n");

    [Test]
    public async Task A_dot_prefixed_tree_with_a_hard_link_unpacks() {
        Write(Archive, Dir("."), Server, Dir("./postgres"), Dir("./postgres/bin"), RegularFile("./postgres/bin/postgres", "pg"),
            Hardlink("./postgres/bin/pg", "./postgres/bin/postgres"));

        await Unpack();

        await Assert.That(File.ReadAllText(Path.Combine(Dest, "postgres/bin/postgres"))).IsEqualTo("pg");
        await Assert.That(File.ReadAllText(Path.Combine(Dest, "postgres/bin/pg"))).IsEqualTo("pg");

        if (!OperatingSystem.IsWindows()) {
            await Assert.That(File.GetUnixFileMode(Path.Combine(Dest, "kcap-server")).HasFlag(UnixFileMode.UserExecute)).IsTrue();
            await Assert.That(File.GetUnixFileMode(Dest)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task An_in_tree_symlink_unpacks() {
        Write(Archive, Server, Dir("./lib"), RegularFile("./lib/libx.so.1", "x"), Symlink("./lib/libx.so", "libx.so.1"));

        await Unpack();

        await Assert.That(File.ReadAllText(Path.Combine(Dest, "lib/libx.so"))).IsEqualTo("x");
    }

    [Test]
    public async Task A_parent_component_is_refused() {
        Write(Archive, Server, RegularFile("./a/../../evil", "x"));
        await AssertRefused("evil");
    }

    [Test]
    public async Task An_absolute_name_is_refused() {
        Write(Archive, Server, RegularFile("/etc/evil", "x"));
        await AssertRefused("/etc/evil");
    }

    [Test]
    public async Task A_symlink_out_of_the_tree_is_refused() {
        Write(Archive, Server, Symlink("./escape", "../../etc"));
        await AssertRefused("escape");
    }

    [Test]
    public async Task An_absolute_symlink_is_refused() {
        Write(Archive, Server, Symlink("./escape", "/etc"));
        await AssertRefused("escape");
    }

    [Test]
    public async Task A_file_cannot_be_written_through_an_earlier_symlink() {
        Write(Archive, Server, Dir("./real"), Symlink("./link", "real"), RegularFile("./link/f", "x"));
        await AssertRefused("link");
        await Assert.That(File.Exists(Path.Combine(Dest, "real", "f"))).IsFalse();
    }

    [Test]
    public async Task A_case_aliased_symlink_chain_cannot_write_beside_the_destination() {
        Write(Archive, Server, Symlink("./a", "."), Symlink("./A/b", ".."), RegularFile("./B/evil", "x"));
        var before = Names();

        // On a case-sensitive volume `A` is a directory of its own and every link stays in the tree.
        if (CaseInsensitive()) await AssertRefused("");
        else await Unpack();

        await Assert.That(Names()).IsEquivalentTo(before);
    }

    [Test]
    public async Task A_chained_symlink_leaving_the_tree_is_refused() {
        Write(Archive, Server, Dir("./d"), Symlink("./d/up", ".."), Symlink("./e", "d/up/.."));
        await AssertRefused("");
    }

    [Test]
    public async Task A_chained_symlink_listed_before_its_hop_is_refused() {
        Write(Archive, Server, Symlink("./e", "d/up/.."), Dir("./d"), Symlink("./d/up", ".."));
        await AssertRefused("");
    }

    [Test]
    public async Task A_name_with_a_nul_is_refused() {
        Write(Archive, Server, RegularFile("./a\0b", "x"));
        await AssertRefused("invalid name");
    }

    bool CaseInsensitive() {
        var probe = Tmp.CreateFile("case-probe", "");
        try { return File.Exists(Tmp.PathTo("CASE-PROBE")); } finally { File.Delete(probe); }
    }

    string[] Names() => Directory.GetFileSystemEntries(Tmp.Path).Select(Path.GetFileName).Where(n => n != "out").Order().ToArray()!;

    [Test]
    public async Task A_hard_link_out_of_the_tree_is_refused() {
        Write(Archive, Server, Hardlink("./h", "../../etc/passwd"));
        await AssertRefused("h");
    }

    [Test]
    public async Task A_hard_link_to_a_later_entry_is_refused() {
        Write(Archive, Server, Hardlink("./h", "./later"), RegularFile("./later", "x"));
        await AssertRefused("h");
    }

    [Test]
    public async Task A_device_entry_is_refused() {
        Write(Archive, Server, ("./dev", TarEntryType.CharacterDevice, null, null));
        await AssertRefused("dev");
    }

    [Test]
    public async Task A_missing_kcap_server_is_refused() {
        Write(Archive, Dir("./lib"), RegularFile("./lib/x", "x"));
        await AssertRefused("kcap-server");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_non_executable_kcap_server_is_refused() {
        Write(Archive, ("./kcap-server", TarEntryType.RegularFile, "x", null, UnixFileMode.UserRead | UnixFileMode.UserWrite));
        await AssertRefused("kcap-server");
    }

    [Test]
    public async Task An_existing_destination_is_refused() {
        Write(Archive, Server);
        Directory.CreateDirectory(Dest);

        await Assert.That(Unpack).Throws<LocalBundleException>();
    }

    [Test]
    public async Task Every_directory_is_synced_after_its_files() {
        Write(Archive, Server, Dir("./a"), RegularFile("./a/f", "x"));
        var synced = new List<string>();

        using (FaultPoints.Arm(p => { if (p.StartsWith("sync:", StringComparison.Ordinal)) synced.Add(p["sync:".Length..]); }))
            await Unpack();

        await Assert.That(synced).Contains(Dest);
        await Assert.That(synced).Contains(Path.Combine(Dest, "a"));
    }
}
