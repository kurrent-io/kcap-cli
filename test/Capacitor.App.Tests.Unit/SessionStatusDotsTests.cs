using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The shared session verdict: who counts as working, and the one status word every surface shows.
public class SessionStatusDotsTests {
    [Test]
    [Arguments("Running", false, null, true)]
    [Arguments("Running", false, 0, true)]
    [Arguments("Running", true, null, false)]
    [Arguments("Running", true, 0, false)]
    [Arguments("Running", true, 1, true)]
    [Arguments("Running", null, null, false)]
    [Arguments("Running", null, 2, true)]
    [Arguments("Starting", false, 2, false)]
    [Arguments("Completed", false, 2, false)]
    public async Task Is_working_across_the_verdict_and_the_count(string status, bool? awaitingInput, int? liveSubagents, bool expected) {
        await Assert.That(SessionStatusDots.IsWorking(status, awaitingInput, liveSubagents)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_remote_row_carries_no_count_and_never_works_by_it() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });

        await Assert.That(row.LiveSubagents).IsNull();
        await Assert.That(SessionStatusDots.IsWorking(row.Status, row.AwaitingInput, row.LiveSubagents)).IsFalse();
    }

    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Local(string id, string status = "Running", bool? awaiting = null, int? subagents = null, string? title = null) =>
        AgentRow.FromLocal(
            new(id, "agent", "claude", "/repo", status, null, null, null, DateTime.UtcNow, null, null,
                Title: title, AwaitingInput: awaiting, LiveSubagents: subagents),
            Repo);

    [Test]
    public async Task One_verdict_ranks_failed_then_needs_you_then_working_then_idle() {
        var failed = SessionStatusDots.ForRow(Local("a", status: "Failed", awaiting: true), pending: false);
        await Assert.That(failed.Kind).IsEqualTo(AgentStatusKind.Failed);
        await Assert.That(failed.Label).IsEqualTo("Failed");

        var pending = SessionStatusDots.ForRow(Local("a", awaiting: false, subagents: 2), pending: true);
        await Assert.That(pending.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
        await Assert.That(pending.AccessibleName).IsEqualTo("Needs you");
        await Assert.That(pending.Tip).Contains("Needs you\nStatus");
        await Assert.That(pending.Tip).Contains("Pending response");
        await Assert.That(pending.Tip).Contains("2 subagents running");
        await Assert.That(pending.Pulses).IsFalse();

        var working = SessionStatusDots.ForRow(Local("a", awaiting: true, subagents: 1), pending: false);
        await Assert.That(working.Kind).IsEqualTo(AgentStatusKind.Working);
        await Assert.That(working.Tip).Contains("Waiting for input.");
        await Assert.That(working.Pulses).IsTrue();

        var idle = SessionStatusDots.ForRow(Local("a", awaiting: true), pending: false);
        await Assert.That(idle.Kind).IsEqualTo(AgentStatusKind.Idle);
        await Assert.That(idle.AccessibleName).IsEqualTo("Idle");
        await Assert.That(idle.Tip).StartsWith("Idle\nStatus");
        await Assert.That(idle.Pulses).IsFalse();

        var done = SessionStatusDots.ForRow(Local("a", status: "Completed"), pending: false);
        await Assert.That(done.Kind).IsEqualTo(AgentStatusKind.Done);
        await Assert.That(done.Label).IsEqualTo("Done");
    }

    [Test]
    public async Task A_collapsed_group_names_how_many_share_the_dominant_status() {
        var idle = Local("a", awaiting: true, title: "First");
        var also = Local("b", awaiting: true, title: "Second");
        var rollup = SessionStatusDots.Rollup([idle, also], new HashSet<string>());
        await Assert.That(rollup).IsNotNull();
        await Assert.That(rollup!.Label).IsEqualTo("Idle");
        await Assert.That(rollup.AccessibleName).IsEqualTo("2 sessions idle");
        await Assert.That(rollup.Tip).Contains("First —");
        await Assert.That(rollup.Tip).Contains("Second —");

        var mixed = SessionStatusDots.Rollup(
            [Local("a", status: "Failed", title: "Broken"), Local("b", awaiting: true, title: "Waiting")],
            new HashSet<string>());
        await Assert.That(mixed!.Label).IsEqualTo("Failed");
        await Assert.That(mixed.AccessibleName).IsEqualTo("1 session failed");
        await Assert.That(mixed.Tip).Contains("Waiting —");

        await Assert.That(SessionStatusDots.Rollup([Local("a")], new HashSet<string>())).IsNull();
    }

    [Test]
    public async Task A_tip_labels_the_session_the_requester_and_the_borrowed_checkout() {
        var row = AgentRow.FromLocal(
            new("a", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null,
                "ada@example.com", AwaitingInput: false, SessionId: "abc123", BorrowedFrom: "/repo/wt"),
            Repo);
        var status = SessionStatusDots.ForRow(row, pending: false);

        await Assert.That(status.Tip.Split('\n')[0]).IsEqualTo("Working");
        await Assert.That(status.Tip).Contains("Working\nStatus");

        var timed = SessionStatusDots.Present(
            "Running", false, false, null, false, null, null, "Working for 2m 39s", sessionId: null);
        await Assert.That(timed.Label).IsEqualTo("Working");
        await Assert.That(timed.Tip).IsEqualTo("Working for 2m 39s\nStatus");

        var asked = SessionStatusDots.Present(
            "Running", true, true, null, pending: true, null, null, null, sessionId: null, answerExpected: true);
        await Assert.That(asked.Kind).IsEqualTo(AgentStatusKind.Answer);
        await Assert.That(asked.Label).IsEqualTo("Answer");
        await Assert.That(asked.Tip).IsEqualTo("An answer is expected\nStatus");
        await Assert.That(asked.Tip).DoesNotContain("Idle");
        await Assert.That(status.Tip).Contains("\n\nabc123\nSession");

        var named = SessionStatusDots.ForRow(row with { Model = "claude-opus-5" }, pending: false);
        await Assert.That(named.Tip).Contains("Claude Opus 5\nModel");
        await Assert.That(status.Tip).Contains("\n\nada@example.com\nRequester");
        await Assert.That(status.Tip).Contains("\n\n/repo/wt\nBorrowed from");
    }
}
