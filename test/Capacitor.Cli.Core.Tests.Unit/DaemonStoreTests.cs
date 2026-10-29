using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonStore"/> — the name-sanitization rule and the per-name path layout
/// that prevents two daemons under the same name from racing on the lock.
/// </summary>
public class DaemonStoreTests {
    [Test]
    [Arguments("laptop", "laptop")]
    [Arguments("LAPTOP", "laptop")]
    [Arguments("My Daemon", "my-daemon")]
    [Arguments("user@host", "user-host")]
    [Arguments("a/b\\c", "a-b-c")]
    [Arguments("  spaced  ", "spaced")]
    [Arguments("dots.are.fine", "dots.are.fine")]
    [Arguments("dashes-ok", "dashes-ok")]
    [Arguments("under_score", "under_score")] // underscores survive: filesystem-safe and common
    [Arguments("collapse---dashes", "collapse-dashes")]
    public async Task Sanitize_NormalisesNames(string input, string expected) {
        await Assert.That(DaemonStore.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(DaemonStore.Sanitize(input)).IsEqualTo("daemon");
    }

    /// <summary>Callers hand on a name that has already been through here — the service id is derived
    /// from it independently — so a second pass has to be a no-op.</summary>
    [Test]
    public async Task Sanitize_IsIdempotent() {
        var once = DaemonStore.Sanitize("a/b c");

        await Assert.That(DaemonStore.Sanitize(once)).IsEqualTo(once);
    }

    [Test]
    public async Task EveryPer_name_path_shares_the_sanitized_name() {
        const string root = "/daemons";

        var paths = new DaemonStore(root);

        await Assert.That(paths.LockPath("My Daemon")).IsEqualTo(Under("my-daemon.lock"));
        await Assert.That(paths.PidPath("My Daemon")).IsEqualTo(Under("my-daemon.pid"));
        await Assert.That(paths.StartLockPath("My Daemon")).IsEqualTo(Under("my-daemon.start"));
        await Assert.That(paths.RestartPendingPath("My Daemon")).IsEqualTo(Under("my-daemon.restart-pending"));
        await Assert.That(paths.VersionPath("My Daemon")).IsEqualTo(Under("my-daemon.version"));
        await Assert.That(paths.SocketPath("My Daemon")).IsEqualTo(Under("my-daemon.sock"));
        await Assert.That(paths.ConsentLogPath("My Daemon")).IsEqualTo(Under("my-daemon", "consent-decisions.jsonl"));

        return;

        static string Under(params ReadOnlySpan<string> parts) => Path.Combine([root, .. parts]);
    }

    [Test, NotInParallel]
    public async Task Default_directory_lives_under_the_daemons_folder() {
        using var env  = EnvScope.Exclusive(DaemonStore.DaemonsDirEnvVar, null);
        using var home = EnvScope.Exclusive("HOME", PinnedHome);

        await Assert.That(DaemonStore.FromEnvironment().Directory).IsEqualTo(DefaultDaemonsDir);
    }

    [Test, NotInParallel]
    public async Task Environment_value_wins_over_the_home_fallback() {
        using var home = EnvScope.Exclusive("HOME", PinnedHome);

        using (var env = EnvScope.Exclusive(DaemonStore.DaemonsDirEnvVar, "/elsewhere"))
            await Assert.That(DaemonStore.FromEnvironment().Directory).IsEqualTo("/elsewhere");

        // Empty reads as unset, or `export KCAP_DAEMONS_DIR=` puts every lock in the cwd.
        using (var env = EnvScope.Exclusive(DaemonStore.DaemonsDirEnvVar, ""))
            await Assert.That(DaemonStore.FromEnvironment().Directory).IsEqualTo(DefaultDaemonsDir);
    }

    /// <summary>The shared <c>.config/kcap</c> prefix is a coincidence, not a derivation — see
    /// <see cref="DaemonStore"/> for the incident that pinned it.</summary>
    [Test, NotInParallel]
    public async Task Config_dir_override_does_not_move_the_daemons_directory() {
        using var daemons = EnvScope.Exclusive(DaemonStore.DaemonsDirEnvVar, null);
        using var config  = EnvScope.Exclusive(ConfigRoot.ConfigDirEnvVar, "/elsewhere/config");
        using var home    = EnvScope.Exclusive("HOME", PinnedHome);

        await Assert.That(DaemonStore.FromEnvironment().Directory).IsEqualTo(DefaultDaemonsDir);
    }

    // Any rooted path does: FromEnvironment only joins strings, so nothing has to exist. Pinned
    // because a Windows runner can export a HOME that differs from the user profile.
    const string PinnedHome = "/pinned-home";

    static string DefaultDaemonsDir => Path.Combine(PinnedHome, ".config", "kcap", "daemons");

    /// <summary>
    /// Pins the socket-path budget. A control socket binds inside the daemons directory and macOS
    /// caps <c>sockaddr_un</c> at 103 characters — measured: <c>bind()</c> succeeds at 103 and fails
    /// at 104. A <see cref="TempDaemonStore"/> plus the longest daemon name any suite uses has to fit,
    /// and on a Mac the overhead is already 72 of those characters
    /// (<c>$TMPDIR</c> 49 + <c>kcap-test-</c> + a 6-char hint + a 6-char random suffix + <c>.sock</c>).
    /// Without this the overflow would be invisible on the Linux CI leg (short <c>/tmp</c>, 108-byte
    /// limit) and only surface on a developer's Mac.
    /// </summary>
    [Test, ExcludeOn(OS.Windows)] // no AF_UNIX path limit to blow, and temp roots differ
    public async Task Temp_socket_path_fits_the_platform_limit() {
        using var daemons = new TempDaemonStore();
        var longestNameInUse = new string('x', 22); // "test-consent-subscribe"

        await Assert.That(daemons.Store.SocketPath(longestNameInUse).Length).IsLessThanOrEqualTo(103);
    }

    /// <summary>Pins that cleanup takes everything a daemon leaves, including state with no daemon file
    /// beside it, a half-written temp file and a daemon named like another's lock, then the directory.</summary>
    [Test]
    public async Task DeleteAll_removes_every_entry_and_the_directory() {
        using var daemons = new TempDaemonStore();
        daemons.CreateFile("vscode.lock", "x");
        daemons.CreateFile("vscode.pid", "1");
        daemons.CreateFile("vscode.service-txn.tmp", "{");
        daemons.CreateFile("vscode/consent.json", "{}");
        daemons.CreateFile("affirmed/reviewers.json", "{}");
        daemons.CreateFile("old.start");
        daemons.CreateFile("foo.lock.pid", "1");
        daemons.CreateFile("foo.lock/consent.json", "{}");

        await Assert.That(daemons.Store.DeleteAll()).IsTrue();
        await Assert.That(Directory.Exists(daemons.Directory)).IsFalse();
    }

    /// <summary>Pins that a held lock stops the whole cleanup: unlinking it would let the next taker lock
    /// a fresh file while the holder still runs.</summary>
    [Test]
    [Arguments("busy.lock")]
    [Arguments("busy.start")]
    [Arguments("busy.service-lock")]
    public async Task DeleteAll_deletes_nothing_while_a_lock_is_held(string lockFile) {
        using var daemons = new TempDaemonStore();
        var pid   = daemons.CreateFile("busy.pid", "1");
        var state = daemons.CreateFile("busy/consent.json", "{}");
        var held  = daemons.CreateFile(lockFile, "x");

        bool gone;
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            gone = daemons.Store.DeleteAll();

        await Assert.That(gone).IsFalse();
        await Assert.That(File.Exists(held)).IsTrue();
        await Assert.That(File.Exists(pid)).IsTrue();
        await Assert.That(File.Exists(state)).IsTrue();
    }

    /// <summary>Pins that a cleanup stopped by a held lock leaves every other lock file too: deleting one
    /// would strip a stopped daemon's state of its lock.</summary>
    [Test]
    public async Task DeleteAll_keeps_earlier_locks_when_a_later_one_is_held() {
        using var daemons = new TempDaemonStore();
        var idleLock = daemons.CreateFile("aaa.lock", "x");
        var idlePid  = daemons.CreateFile("aaa.pid", "1");
        var busyLock = daemons.CreateFile("zzz.lock", "x");

        bool gone;
        using (new FileStream(busyLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            gone = daemons.Store.DeleteAll();

        await Assert.That(gone).IsFalse();
        await Assert.That(File.Exists(idleLock)).IsTrue();
        await Assert.That(File.Exists(idlePid)).IsTrue();
        await Assert.That(daemons.Store.DeleteAll()).IsTrue();
    }

    /// <summary>Pins that a link in the directory is removed, never followed: its target, wherever it is,
    /// survives, and a dangling one does not stop the cleanup.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task DeleteAll_removes_links_without_touching_their_targets() {
        using var daemons = new TempDaemonStore();
        using var outside = new TempDir("outside");
        var target = outside.CreateFile("keep.lock", "mine");
        File.CreateSymbolicLink(Path.Combine(daemons.Directory, "evil.lock"), target);
        File.CreateSymbolicLink(Path.Combine(daemons.Directory, "gone.pid"), Path.Combine(outside.Path, "missing"));
        Directory.CreateSymbolicLink(Path.Combine(daemons.Directory, "state"), outside.Path);

        await Assert.That(daemons.Store.DeleteAll()).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("mine");
        await Assert.That(Directory.Exists(daemons.Directory)).IsFalse();
    }
}
