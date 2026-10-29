using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
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

    /// Working is not a foreground turn on its own; it takes the daemon's attestation, and a
    /// pending card outranks both.
    [Test]
    [Arguments(false, true, false, true)]
    [Arguments(false, false, false, false)]
    [Arguments(true, true, false, false)]
    [Arguments(false, true, true, false)]
    public async Task A_foreground_turn_needs_working_and_the_daemons_attestation(
            bool awaitingInput, bool turnAttested, bool pending, bool expected) {
        var status = SessionStatusDots.Present("Running", awaitingInput, waitsOnUser: false, liveSubagents: null, pending,
            usageLimitSummary: null, launchStage: null, elapsed: null, turnAttested: turnAttested);

        await Assert.That(status.InForegroundTurn).IsEqualTo(expected);
    }

    [Test]
    public async Task A_remote_row_carries_no_count_and_never_works_by_it() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });

        await Assert.That(row.LiveSubagents).IsNull();
        await Assert.That(SessionStatusDots.IsWorking(row.Status, row.AwaitingInput, row.LiveSubagents)).IsFalse();
    }

    [Test]
    public async Task A_row_carries_the_account_its_lane_reports() {
        var remote = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude",
            AccountDirectory = "/home/me/.claude-work", AccountId = "acc1", AccountLabel = "work",
        });
        var local = AgentRow.FromLocal(
            new("a1", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null,
                AccountDirectory: "/home/me/.claude", AccountId: null, AccountLabel: ".claude"),
            new("path:/repo", "repo"));

        await Assert.That((remote.AccountDirectory, remote.AccountId, remote.AccountLabel)).IsEqualTo(("/home/me/.claude-work", "acc1", "work"));
        await Assert.That((local.AccountDirectory, local.AccountId, local.AccountLabel)).IsEqualTo(("/home/me/.claude", (string?)null, ".claude"));
    }

    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    [Test]
    [Arguments("idle")]
    [Arguments("Idle")]
    public async Task Explicit_remote_idle_matches_the_local_idle_indicator(string status) {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = status, DaemonName = "rail-qa", Vendor = "claude",
        });
        var remote = SessionStatusDots.ForRow(row, pending: false);
        var local = SessionStatusDots.ForRow(Local("a1", awaiting: true), pending: false);
        await Assert.That(remote.Kind).IsEqualTo(local.Kind);
        await Assert.That(remote.ShowsClock).IsTrue();
        await Assert.That(remote.ShowsDash).IsFalse();
        await Assert.That(remote.IsWarning).IsTrue();
        // Presentation parity does not fabricate the local daemon's input/attention facts.
        await Assert.That(row.AwaitingInput).IsNull();
        await Assert.That(SessionStatusDots.NeedsAttention(row)).IsFalse();
        await Assert.That(SessionStatusDots.ForRow(row, pending: true).Kind).IsEqualTo(AgentStatusKind.NeedsYou);
        await Assert.That(SessionStatusDots.ForRow(row, pending: false, answerExpected: true).Kind).IsEqualTo(AgentStatusKind.Answer);
    }

    static AgentRow Local(string id, string status = "Running", bool? awaiting = null, int? subagents = null, string? title = null) =>
        AgentRow.FromLocal(
            new(id, "agent", "claude", "/repo", status, null, null, null, DateTime.UtcNow, null, null,
                Title: title, AwaitingInput: awaiting, LiveSubagents: subagents),
            Repo);

    /// A dialog on the terminal blocks the agent whatever the turn verdict says, so a row reads
    /// Needs you even mid-turn, and the worktree counts it.
    [Test]
    public async Task A_terminal_dialog_makes_a_working_row_need_you() {
        var dialog = new TerminalDialogDto("Trust this MCP server?", "", ["Yes", "No"], 0, "");
        var row = AgentRow.FromLocal(
            new("a", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null,
                AwaitingInput: false, TerminalDialog: dialog),
            Repo);

        var status = SessionStatusDots.ForRow(row, pending: false);
        await Assert.That(status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
        await Assert.That(status.Tip).Contains("Waiting for you in Terminal");
        await Assert.That(status.Tip).DoesNotContain("Pending response");
        await Assert.That(SessionStatusDots.NeedsAttention(row)).IsTrue();
        await Assert.That(SessionStatusDots.NeedsAttention(row with { TerminalDialog = null })).IsFalse();
    }

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
        await Assert.That(done.ShowsDash).IsTrue();
        await Assert.That(done.ShowsRing).IsFalse();
    }

    /// A removed remote row republishes its last word, Running, as ended: it must read settled.
    [Test]
    public async Task An_ended_running_session_shows_the_dash_not_the_ring() {
        var live = SessionStatusDots.Present("Running", null, false, null, false, null, null, null);
        var ended = SessionStatusDots.Present("Running", null, false, null, false, null, null, null, ended: true);

        await Assert.That(live.ShowsRing).IsTrue();
        await Assert.That(ended.Label).IsEqualTo("Running");
        await Assert.That(ended.ShowsRing).IsFalse();
        await Assert.That(ended.IsInFlight).IsFalse();
        await Assert.That(ended.ShowsDash).IsTrue();
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
    public async Task A_tip_labels_the_requester_and_the_borrowed_checkout_but_not_the_session_id() {
        var row = AgentRow.FromLocal(
            new("a", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null,
                "ada@example.com", AwaitingInput: false, SessionId: "abc123", BorrowedFrom: "/repo/wt"),
            Repo);
        var status = SessionStatusDots.ForRow(row, pending: false);

        await Assert.That(status.Tip.Split('\n')[0]).IsEqualTo("Working");
        await Assert.That(status.Tip).Contains("Working\nStatus");

        var timed = SessionStatusDots.Present(
            "Running", false, false, null, false, null, null, "Working for 2m 39s");
        await Assert.That(timed.Label).IsEqualTo("Working");
        await Assert.That(timed.Tip).IsEqualTo("Working for 2m 39s\nStatus");

        var asked = SessionStatusDots.Present(
            "Running", true, true, null, pending: true, null, null, null, answerExpected: true);
        await Assert.That(asked.Kind).IsEqualTo(AgentStatusKind.Answer);
        await Assert.That(asked.Label).IsEqualTo("Answer");
        await Assert.That(asked.Tip).IsEqualTo("An answer is expected\nStatus");
        await Assert.That(asked.Tip).DoesNotContain("Idle");
        await Assert.That(status.Tip).DoesNotContain("abc123");

        var named = SessionStatusDots.ForRow(row with { Model = "claude-opus-5" }, pending: false);
        await Assert.That(named.Tip).Contains("Claude Opus 5\nModel");
        await Assert.That(named.Tip).Contains("Claude Code\nHarness");
        await Assert.That(status.Tip).Contains("\n\nada@example.com\nRequester");
        await Assert.That(status.Tip).Contains("\n\n/repo/wt\nBorrowed from");
    }

    [Test]
    public async Task A_resuming_row_is_in_flight_and_pulses() {
        var status = SessionStatusDots.ForRow(Local("a", status: "Resuming"), pending: false);

        await Assert.That(status.Kind).IsEqualTo(AgentStatusKind.Resuming);
        await Assert.That(status.Label).IsEqualTo("Resuming");
        await Assert.That(status.IsInFlight).IsTrue();
        await Assert.That(status.Pulses).IsTrue();
        await Assert.That(status.ShowsRing).IsTrue();
        await Assert.That(status.IsWarning).IsFalse();
        await Assert.That(status.Tip).Contains("Resuming after a daemon restart");
    }

    [Test]
    public async Task An_interrupted_row_needs_attention_and_names_the_reason() {
        var row = AgentRow.FromLocal(
            new("a", "agent", "claude", "/repo", "Interrupted", null, null, null, DateTime.UtcNow, null, null, Interruption: "too_old"),
            Repo);

        var status = SessionStatusDots.ForRow(row, pending: false);

        await Assert.That(row.Interruption).IsEqualTo("too_old");
        await Assert.That(status.Kind).IsEqualTo(AgentStatusKind.Interrupted);
        await Assert.That(status.Label).IsEqualTo("Interrupted");
        await Assert.That(status.IsWarning).IsTrue();
        await Assert.That(status.ShowsPause).IsTrue();
        await Assert.That(status.IsInFlight).IsFalse();
        await Assert.That(status.Pulses).IsFalse();
        await Assert.That(status.Facts.Select(f => (f.Text, f.Caption))).Contains(("too_old", "Reason"));
        await Assert.That(SessionStatusDots.NeedsAttention(row)).IsTrue();
    }

    [Test]
    public async Task An_interrupted_row_with_no_reason_still_says_how_to_retry() {
        var status = SessionStatusDots.ForRow(Local("a", status: "Interrupted"), pending: false);

        await Assert.That(status.Tip).Contains("kcap agent resume a");
        await Assert.That(status.Facts.Any(f => f.Caption == "Reason")).IsFalse();
    }

    [Test]
    public async Task A_rollup_puts_an_interrupted_session_ahead_of_working_and_resuming_ones() {
        var rows = new[] { Local("w", awaiting: false), Local("r", status: "Resuming"), Local("i", status: "Interrupted") };

        var rollup = SessionStatusDots.Rollup(rows, new HashSet<string>())!;
        var resuming = SessionStatusDots.Rollup([Local("r", status: "Resuming"), Local("s", status: "Resuming")], new HashSet<string>())!;

        await Assert.That(rollup.Kind).IsEqualTo(AgentStatusKind.Interrupted);
        await Assert.That(rollup.AccessibleName).IsEqualTo("1 session interrupted");
        await Assert.That(resuming.AccessibleName).IsEqualTo("2 sessions resuming");
    }
}
