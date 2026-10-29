using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudeLauncherResumeTests {
    const string Pin    = "11111111-2222-3333-4444-555555555555";
    const string Resume = "99999999-8888-7777-6666-555555555555";

    [TempHome] public required TempHome Home { get; init; }

    ClaudeLauncher NewLauncher() =>
        new(new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" }, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);

    static LauncherContext NewCtx(string? pinned, string? resume) =>
        new(
            AgentId:        "a-1",
            SourceRepoPath: "/tmp/repo",
            Worktree:       new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt:         "build it",
            Model:          "sonnet",
            Effort:         "high",
            Tools:          null,
            IsReview:       false,
            IsReviewFlow:   false,
            Review:         null,
            ReviewLaunch:   null
        ) { PermissionMode = "acceptEdits", PinnedSessionId = pinned, ResumeSessionId = resume };

    [Test]
    public async Task Fresh_launch_pins_the_session_id() {
        var args = NewLauncher().BuildArgs(NewCtx(Pin, null)).Args;

        await Assert.That(args[Array.IndexOf(args, "--session-id") + 1]).IsEqualTo(Pin);
        await Assert.That(args).DoesNotContain("--resume");
    }

    /// <summary>A resume of an agent that was waiting sends no turn: an empty prompt adds no argument.</summary>
    [Test]
    public async Task A_resume_with_an_empty_prompt_passes_no_prompt_argument() {
        var args = NewLauncher().BuildArgs(NewCtx(null, Resume) with { Prompt = "" }).Args;

        await Assert.That(args).DoesNotContain("--");
        await Assert.That(args).DoesNotContain("");
    }

    [Test]
    public async Task Resume_launch_passes_resume_and_no_session_id() {
        var args = NewLauncher().BuildArgs(NewCtx(null, Resume)).Args;

        await Assert.That(args[Array.IndexOf(args, "--resume") + 1]).IsEqualTo(Resume);
        await Assert.That(args).DoesNotContain("--session-id");
    }

    /// The resume ledger can hold the dashless id transcript discovery records; Claude resumes only a dashed UUID.
    [Test]
    public async Task Resume_restores_the_dashes_of_a_dashless_session_id() {
        var args = NewLauncher().BuildArgs(NewCtx(null, Resume.Replace("-", ""))).Args;

        await Assert.That(args[Array.IndexOf(args, "--resume") + 1]).IsEqualTo(Resume);
    }

    [Test]
    public async Task Resume_keeps_the_other_flags_in_order() {
        var args = NewLauncher().BuildArgs(NewCtx(null, Resume)).Args;

        await Assert.That(args.SequenceEqual([
            "--resume", Resume, "--permission-mode", "acceptEdits", "--effort", "high", "--model", "sonnet", "--", "build it"
        ])).IsTrue();
    }

    [Test]
    public async Task Launch_without_a_pin_or_resume_is_unchanged() {
        var args = NewLauncher().BuildArgs(NewCtx(null, null)).Args;

        await Assert.That(args.SequenceEqual([
            "--permission-mode", "acceptEdits", "--effort", "high", "--model", "sonnet", "--", "build it"
        ])).IsTrue();
        await Assert.That(NewLauncher().SupportsResume).IsTrue();
    }
}
