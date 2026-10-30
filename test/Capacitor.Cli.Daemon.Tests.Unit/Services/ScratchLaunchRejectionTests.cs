using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A scratch launch is accepted only as a review-flow reviewer naming nothing to read.</summary>
public class ScratchLaunchRejectionTests {
    static LaunchAgentCommand Scratch() =>
        new("agent-1", "review this", "", null, "", null, null, "claude", LaunchKind.ReviewFlow, ScratchWorkspace: true);

    [Test] public async Task A_bare_scratch_review_flow_launch_passes() =>
        await Assert.That(AgentOrchestrator.ScratchLaunchRejection(Scratch())).IsNull();

    [Test] public async Task A_launch_without_scratch_is_not_judged() =>
        await Assert.That(AgentOrchestrator.ScratchLaunchRejection(Scratch() with { ScratchWorkspace = false, RepoPath = "/repo" })).IsNull();

    [Test] public async Task An_interactive_scratch_launch_is_refused() =>
        await Assert.That(AgentOrchestrator.ScratchLaunchRejection(Scratch() with { Kind = LaunchKind.Default })).IsNotNull();

    [Test]
    [Arguments("repo")]
    [Arguments("borrowed")]
    [Arguments("borrow-cwd")]
    [Arguments("base-ref")]
    [Arguments("attachments")]
    public async Task A_scratch_launch_naming_something_to_read_is_refused(string what) {
        var cmd = what switch {
            "repo"       => Scratch() with { RepoPath = "/repo" },
            "borrowed"   => Scratch() with { Borrowed = true },
            "borrow-cwd" => Scratch() with { BorrowCwd = "/repo" },
            "base-ref"   => Scratch() with { BaseRef = "main" },
            _            => Scratch() with { AttachmentIds = ["patch-1"] }
        };

        await Assert.That(AgentOrchestrator.ScratchLaunchRejection(cmd)).IsNotNull();
    }
}
