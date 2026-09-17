using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A service unit carries the server URL, the profile, and possibly a command that produces a
/// credential. <see cref="File.WriteAllText(string,string)"/> defaults to <c>-rw-r--r--</c> — verified on
/// a real launchd install — so the write path has to establish owner-only mode itself, and prove it.
/// </summary>
public partial class ServiceFilesTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint umask(uint mask);

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_writes_the_content_and_leaves_it_owner_only() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("unit.plist");
        ServiceFiles.WriteOwnerOnly(path, "<plist>KCAP_COPILOT_TOKEN_CMD</plist>");

        await Assert.That(await File.ReadAllTextAsync(path))
            .IsEqualTo("<plist>KCAP_COPILOT_TOKEN_CMD</plist>");

        Skip.When(OperatingSystem.IsWindows(), "POSIX modes; Windows inherits the directory ACL");
        await Assert.That(File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The default write mode really is world-readable, so the assertion above is not vacuous.
    /// Without this, a platform that already wrote 0600 would make the whole file pass while the write
    /// path did nothing.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task The_default_write_mode_is_world_readable_so_the_fix_is_load_bearing() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var path = tmp.PathTo("unit.plist");
        await File.WriteAllTextAsync(path, "<plist/>");

        await Assert.That(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead)).IsTrue()
            .Because("if this stops being true, WriteOwnerOnly is no longer what protects the unit");
    }

    /// <summary>Overwriting an existing world-readable unit ends up owner-only rather than inheriting the
    /// old mode, and no staging file is left beside it.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_overwrites_a_world_readable_unit_and_leaves_no_staging_file() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("unit.plist");
        await File.WriteAllTextAsync(path, "old");
        ServiceFiles.WriteOwnerOnly(path, "new");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new");
        await Assert.That(Directory.GetFiles(tmp.Path).Length).IsEqualTo(1)
            .Because("the staging file must be moved, not left beside the unit");

        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");
        await Assert.That(File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead)).IsFalse();
    }

    /// <summary>The mode is EXACTLY owner read+write under either a permissive or a restrictive umask.
    ///
    /// <para>Both directions matter and a group/other-bits-only assertion catches neither. A permissive
    /// umask (0000) is the leak case. A restrictive one (0777) is the opposite failure:
    /// <c>UnixCreateMode</c> is filtered through the umask, so the file can land <c>0000</c> — which no
    /// "nothing extra than owner-only" check rejects, and which launchd cannot read, so the install would
    /// report success and produce a service that never starts.</para>
    ///
    /// <para>Serialized because the umask is process-global.</para></summary>
    [Test]
    [NotInParallel]
    [Arguments(0u)]
    [Arguments(0x3Fu)]    // umask 077
    [Arguments(0x1FFu)]   // umask 777
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_produces_exactly_owner_read_write_under_any_umask(uint mask) {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var path     = tmp.PathTo("unit.plist");
        var previous = umask(mask);
        try {
            ServiceFiles.WriteOwnerOnly(path, "SECRET-COMMAND");

            await Assert.That(File.GetUnixFileMode(path))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // The property that actually matters to launchd: the owner can still read it.
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("SECRET-COMMAND");
        } finally {
            _ = umask(previous);
        }
    }

    /// <summary>If the final mode cannot be guaranteed, no unit is left at the live path.
    ///
    /// <para>The failure is injected, because on a normal filesystem a rename preserves the mode and this
    /// branch is unreachable. It is worth proving anyway: an earlier revision ran the post-rename check
    /// outside the cleanup scope, so a failure there threw while leaving a readable credential-bearing unit
    /// exactly where launchd would consume it — a failed install that still published the secret.</para></summary>
    [Test]
    public async Task WriteOwnerOnly_removes_the_live_unit_when_the_final_check_fails() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("unit.plist");
        var ex = Assert.Throws<InvalidOperationException>(() => ServiceFiles.WriteOwnerOnly(
            path, "SECRET-COMMAND", null,
            verifyFinal: _ => throw new InvalidOperationException("mode could not be guaranteed")));

        await Assert.That(ex!.Message).Contains("guaranteed");
        await Assert.That(File.Exists(path)).IsFalse()
            .Because("a failed install must not leave a unit at the path launchd reads");
        await Assert.That(Directory.GetFiles(tmp.Path)).IsEmpty()
            .Because("the staging file must not survive either");
    }

    /// <summary>A directory the writer creates itself is usable, whatever the umask.
    ///
    /// <para>umask 002 is the default for a user whose primary group matches their own name — the
    /// <c>pam_umask</c> usergroups behaviour Debian and Ubuntu enable — so a directory created under it
    /// lands 0775. The writer creates the unit directory, so a check that rejects group-write outright
    /// rejects the writer's own work and no install can succeed on those distributions.</para></summary>
    [Test]
    [NotInParallel]
    [Arguments(0x2u)]      // umask 002 — group-writable
    [Arguments(0u)]        // umask 000 — group- and world-writable
    [Arguments(0x100u)]    // umask 400 — strips owner READ, leaving a directory nothing can enumerate
    [Arguments(0x1FFu)]    // umask 777 — strips every bit, leaving one nothing can be written into
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_creates_a_usable_unit_directory_under_any_umask(uint mask) {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var path     = tmp.PathTo("systemd", "user", "kcap-daemon-test.service");
        var dir      = tmp.PathTo("systemd", "user");
        var previous = umask(mask);
        try {
            ServiceFiles.WriteOwnerOnly(path, "SECRET-COMMAND");
        } finally {
            _ = umask(previous);
        }

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("SECRET-COMMAND");

        // EXACTLY owner rwx, both directions. Too permissive lets another account replace the unit; too
        // restrictive is the opposite failure — the requested mode is filtered through the umask like any
        // other, so the owner's own read or write bit can be stripped, and a directory the owner cannot
        // enumerate is one `service list` fails on after an install that reported success.
        await Assert.That(File.GetUnixFileMode(dir)).IsEqualTo(OwnerOnlyDir);
        await Assert.That(File.GetUnixFileMode(tmp.PathTo("systemd"))).IsEqualTo(OwnerOnlyDir)
            .Because("a writable parent is a rename away from replacing the whole unit directory");
    }

    /// <summary>A directory that arrives group- or world-writable is tightened rather than refused — the
    /// write bits for other accounts are what the check is about, and on a directory we own they can simply
    /// be removed. The unit still lands, and the directory no longer grants anyone else write.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_tightens_a_shared_writable_directory_it_can_repair() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        File.SetUnixFileMode(tmp.Path,
            UnixFileMode.UserRead   | UnixFileMode.UserWrite   | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead  | UnixFileMode.GroupWrite  | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead  | UnixFileMode.OtherWrite  | UnixFileMode.OtherExecute);

        ServiceFiles.WriteOwnerOnly(tmp.PathTo("unit.plist"), "SECRET-COMMAND");

        await Assert.That(await File.ReadAllTextAsync(tmp.PathTo("unit.plist"))).IsEqualTo("SECRET-COMMAND");
        await Assert.That(File.GetUnixFileMode(tmp.Path) & SharedWrite).IsEqualTo(default(UnixFileMode));
        await Assert.That(File.GetUnixFileMode(tmp.Path).HasFlag(UnixFileMode.OtherRead)).IsTrue()
            .Because("only the write bits are the hazard; read and traverse are left as the operator set them");
    }

    /// <summary>When the write bits cannot be removed, the install is refused and no unit is left behind:
    /// owner-only mode on the unit is no protection if someone else can replace the unit and choose what
    /// the daemon runs.
    ///
    /// <para>The repair is suppressed rather than provoked. Reaching this for real needs a directory the
    /// current user does not own, and the only ones a test could rely on finding are shared system
    /// directories that a run as root would then really chmod.</para></summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_refuses_a_shared_writable_directory_it_cannot_repair() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        try {
            File.SetUnixFileMode(tmp.Path,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(
                () => ServiceFiles.WriteOwnerOnly(tmp.PathTo("unit.plist"), "x", null,
                    tightenDirectory: _ => { }));

            await Assert.That(ex!.Message).Contains("writable");
            await Assert.That(File.Exists(tmp.PathTo("unit.plist"))).IsFalse();
        } finally {
            // Restored before the TempDir is disposed — the recursive delete needs the mode back.
            try {
                File.SetUnixFileMode(tmp.Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { /* best-effort */ }
        }
    }

    /// <summary>A world-writable directory anywhere above the unit directory is refused, and nothing is
    /// written: an account that can write a parent can rename the unit directory away and supply its own,
    /// so the unit's own mode buys nothing.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_refuses_a_world_writable_ancestor() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var parent    = tmp.CreateDir("parent").Path;
        var path      = tmp.PathTo("parent", "user", "unit.plist");
        try {
            File.SetUnixFileMode(parent,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(() => ServiceFiles.WriteOwnerOnly(path, "x"));

            await Assert.That(ex!.Message).Contains("world-writable");
            await Assert.That(ex.Message).Contains(parent);
            await Assert.That(File.Exists(path)).IsFalse();
        } finally {
            try {
                File.SetUnixFileMode(parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { /* best-effort */ }
        }
    }

    /// <summary>A group-writable ancestor is NOT refused. umask 002 is the default wherever a user's
    /// primary group is their own name, so group-write on the path to a home directory is the ordinary
    /// case — refusing on it would fail the install this whole check exists to let through.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteOwnerOnly_allows_a_group_writable_ancestor() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var parent    = tmp.CreateDir("parent").Path;
        var path      = tmp.PathTo("parent", "user", "unit.plist");
        File.SetUnixFileMode(parent,
            UnixFileMode.UserRead  | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

        ServiceFiles.WriteOwnerOnly(path, "SECRET-COMMAND");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("SECRET-COMMAND");
    }

    const UnixFileMode SharedWrite  = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    const UnixFileMode OwnerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>A pre-existing entry at the staging path is not followed or truncated — the staging inode
    /// is created exclusively. The name carries a full GUID, so this asserts the mechanism rather than a
    /// realistic collision.</summary>
    [Test]
    public async Task WriteOwnerOnly_leaves_an_unrelated_file_in_the_directory_alone() {
        using var tmp = new TempDir();
        var path      = tmp.PathTo("unit.plist");
        var bystander = tmp.PathTo("unit.plist.tmp-not-ours");
        await File.WriteAllTextAsync(bystander, "PRE-EXISTING");
        ServiceFiles.WriteOwnerOnly(path, "new");

        await Assert.That(await File.ReadAllTextAsync(bystander)).IsEqualTo("PRE-EXISTING");
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new");
    }

    // ── manager wiring ───────────────────────────────────────────────────────────────────────────
    //
    // Each manager's Install invokes launchctl/systemctl/schtasks, so the write half is split out and
    // driven with an injected writer. Without this, all of the above could hold while a manager still
    // called File.WriteAllText directly.

    [Test]
    public async Task Launchd_writes_its_plist_through_the_secure_writer() {
        var seen = new List<string>();
        var mgr  = new LaunchdServiceManager(Home, TimeProvider.System, (path, content, _) => seen.Add(path + "|" + content));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0]).Contains(".plist");
        await Assert.That(seen[0]).Contains("KCAP_COPILOT_TOKEN_CMD");
    }

    [Test]
    public async Task Systemd_writes_its_unit_through_the_secure_writer() {
        var seen = new List<string>();
        var mgr  = new SystemdServiceManager(Home, (path, content, _) => seen.Add(path + "|" + content));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0]).Contains(".service");
        await Assert.That(seen[0]).Contains("KCAP_COPILOT_TOKEN_CMD");
    }

    /// <summary>Windows writes two files and both go through the writer — the task XML as UTF-16, the
    /// wrapper as UTF-8. (The token command itself is excluded from the captured environment on Windows;
    /// this asserts the write wiring, not that variable.)</summary>
    [Test]
    public async Task Windows_writes_both_units_through_the_secure_writer() {
        var seen = new List<(string Path, Encoding? Encoding)>();
        var mgr  = new WindowsScheduledTaskServiceManager(Config.Root, (path, _, encoding) => seen.Add((path, encoding)));

        mgr.WriteUnitFiles(Spec());

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen.Any(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal) && Equals(f.Encoding, Encoding.Unicode))).IsTrue();
        await Assert.That(seen.Any(f => f.Path.EndsWith(".cmd", StringComparison.Ordinal) && Equals(f.Encoding, Encoding.UTF8))).IsTrue();
    }

    static ServiceSpec Spec() => new(
        ServiceId:        "test",
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath:          "/tmp/kcap-test.log",
        Environment:      new Dictionary<string, string> { ["KCAP_COPILOT_TOKEN_CMD"] = "gh auth token" },
        ExtraArgs:        []);
}
