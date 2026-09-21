using System.Text;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiReviewerLaunchDirTests {
    [Test]
    public async Task Create_writes_every_file_and_the_sessions_directory() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();

        var paths = PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{\"root\":\"/w\"}");

        await Assert.That(File.ReadAllText(paths.Extension)).IsEqualTo(PiReviewerExtension.Content);
        await Assert.That(File.ReadAllText(paths.Manifest)).IsEqualTo("{\"root\":\"/w\"}");
        await Assert.That(File.ReadAllText(paths.SystemPrompt)).IsEqualTo(PiReviewerSystemPrompt.Text);
        await Assert.That(Directory.Exists(paths.Sessions)).IsTrue();
        await Assert.That(paths.Ready).IsEqualTo(Path.Combine(paths.Dir, PiReviewerExtension.ReadyFileName));
        await Assert.That(File.Exists(paths.Ready)).IsFalse();
    }

    [Test]
    public async Task The_directory_and_the_manifest_are_owner_only() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix file modes.");
        using var state = new TempDir();
        var paths = PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{}");

        if (!OperatingSystem.IsWindows()) {
            await Assert.That(File.GetUnixFileMode(paths.Dir))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.That(File.GetUnixFileMode(paths.Manifest))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task Create_refuses_a_launch_root_left_group_or_other_readable() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Unix file modes.");
        using var state = new TempDir();
        var root = PiReviewerLaunchDir.RootFor(state.Path);
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{}"));

        await Assert.That(ex!.Message).StartsWith("pi_reviewer_launch_dir_not_owner_only");
    }

    [Test]
    public async Task A_refused_create_leaves_no_launch_dir_behind() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();

        // An unpaired surrogate cannot be UTF-8 encoded, so the manifest write inside the
        // all-or-nothing block throws without any filesystem trickery.
        await Assert.ThrowsAsync<EncoderFallbackException>(() => {
            PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "\uD800");
            return Task.CompletedTask;
        });

        var dir = Path.Combine(
            PiReviewerLaunchDir.RootFor(state.Path), PiReviewerLaunchDir.NameFor("epoch1", "agent-1"));
        await Assert.That(Directory.Exists(dir)).IsFalse();
    }

    [Test]
    public async Task Create_replaces_a_leftover_directory_of_the_same_name() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();
        var first = PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{}");
        File.WriteAllText(first.Ready, "stale");

        var second = PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{}");

        await Assert.That(File.Exists(second.Ready)).IsFalse();
    }

    [Test]
    public async Task Delete_removes_the_directory_and_refuses_a_path_outside_the_root() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();
        var paths   = PiReviewerLaunchDir.Create(state.Path, "epoch1", "agent-1", "{}");
        var outside = Directory.CreateDirectory(Path.Combine(state.Path, "not-a-launch-dir")).FullName;

        PiReviewerLaunchDir.Delete(paths.Dir, state.Path);
        PiReviewerLaunchDir.Delete(outside, state.Path);

        await Assert.That(Directory.Exists(paths.Dir)).IsFalse();
        await Assert.That(Directory.Exists(outside)).IsTrue();
    }

    [Test]
    public async Task SweepStale_removes_other_epochs_and_keeps_the_current_one() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();
        var old   = PiReviewerLaunchDir.Create(state.Path, "epochOld", "agent-1", "{}");
        var live  = PiReviewerLaunchDir.Create(state.Path, "epochNow", "agent-2", "{}");

        PiReviewerLaunchDir.SweepStale(state.Path, "epochNow");

        await Assert.That(Directory.Exists(old.Dir)).IsFalse();
        await Assert.That(Directory.Exists(live.Dir)).IsTrue();
    }

    [Test]
    public async Task A_launch_id_cannot_escape_the_root() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Owner-only directories are POSIX-only.");
        using var state = new TempDir();

        var paths = PiReviewerLaunchDir.Create(state.Path, "epoch1", "../../evil", "{}");

        await Assert.That(Path.GetFullPath(paths.Dir))
            .StartsWith(Path.GetFullPath(PiReviewerLaunchDir.RootFor(state.Path)) + Path.DirectorySeparatorChar);
    }
}
