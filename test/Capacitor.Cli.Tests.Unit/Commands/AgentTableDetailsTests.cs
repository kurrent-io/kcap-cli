using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap agent ls</c> beside the desktop app: the process state apart from what the agent is doing,
/// the harness, the session apart from the agent id, and the worktree apart from the repository.
/// </summary>
public class AgentTableDetailsTests {
    const string Id = "0b6ee11919594412a1b2c3d4e5f60718";

    static readonly string Root     = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
    static readonly string Repo     = Path.Combine(Root, "shop-api");
    static readonly string Worktree = Path.Combine(Repo, ".capacitor", "worktrees", "agent-0b6ee119195944");

    static AgentStatusDto Detail(bool? awaitingInput, string status = "Running", string? worktree = null) =>
        new(Id, "agent", "claude", Repo, status, null, null, null, DateTime.UnixEpoch, null, null,
            WorktreePath: worktree ?? Worktree, SessionId: "5f0c2a7e-1111-4222-8333-944455556666", AwaitingInput: awaitingInput);

    static string[] Table(AgentStatusDto? detail) =>
        [.. AgentCommand.FormatAgentTable(
            [new AgentRow(Id, "Running", Repo, "agent", "", "", "Fix the flaky test", "running")],
            detail is null ? null : new Dictionary<string, AgentStatusDto> { [Id] = detail })];

    [Test]
    public async Task A_running_agent_waiting_for_a_prompt_reads_idle_beside_its_running_process() {
        var lines = Table(Detail(awaitingInput: true));

        await Assert.That(lines[0]).Contains("ACTIVITY");
        await Assert.That(lines[1]).Contains("Running");
        await Assert.That(lines[1]).Contains(" idle ");
    }

    [Test]
    [Arguments(false, "working")]
    [Arguments(null, "")]
    public async Task Activity_follows_the_daemons_turn_verdict(bool? awaitingInput, string expected) {
        await Assert.That(AgentCommand.Activity(Detail(awaitingInput))).IsEqualTo(expected);
    }

    [Test]
    public async Task A_stopped_process_has_no_activity() {
        await Assert.That(AgentCommand.Activity(Detail(awaitingInput: true, status: "Interrupted"))).IsEqualTo("");
    }

    [Test]
    public async Task The_harness_session_and_worktree_are_their_own_columns() {
        var lines = Table(Detail(awaitingInput: true));

        await Assert.That(lines[0]).Contains("HARNESS");
        await Assert.That(lines[0]).Contains("SESSION");
        await Assert.That(lines[0]).Contains("WORKTREE");
        await Assert.That(lines[1]).Contains(" claude ");
        await Assert.That(lines[1]).Contains("5f0c2a7e-1111-4222-8333-944455556666");
        await Assert.That(lines[1]).Contains(" agent-0b6ee119195944 ");
        await Assert.That(lines[1]).EndsWith("Fix the flaky test");
    }

    [Test]
    public async Task The_agent_column_is_as_wide_as_its_longest_id() {
        var lines = Table(null);

        await Assert.That(lines[0]).StartsWith("AGENT" + new string(' ', Id.Length - "AGENT".Length + 1) + "STATUS");
        await Assert.That(lines[1]).StartsWith(Id + " Running");
    }

    [Test]
    public async Task Without_the_daemons_details_the_table_adds_no_empty_columns() {
        var header = Table(null)[0];

        await Assert.That(header).DoesNotContain("ACTIVITY");
        await Assert.That(header).DoesNotContain("HARNESS");
        await Assert.That(header).DoesNotContain("SESSION");
        await Assert.That(header).DoesNotContain("WORKTREE");
    }

    [Test]
    public async Task An_agent_working_in_the_repository_itself_shows_no_worktree() {
        await Assert.That(AgentCommand.WorktreeCell(Repo, Repo)).IsEqualTo("");
        await Assert.That(AgentCommand.WorktreeCell(Repo, Worktree)).IsEqualTo("agent-0b6ee119195944");

        var elsewhere = Path.Combine(Root, "borrowed");
        await Assert.That(AgentCommand.WorktreeCell(Repo, elsewhere)).IsEqualTo(elsewhere);
    }

    [Test]
    public async Task A_sibling_folder_sharing_the_worktrees_prefix_keeps_its_full_path() {
        var sibling = Path.Combine(Repo, ".capacitor", "worktrees-backup", "agent");

        await Assert.That(AgentCommand.WorktreeCell(Repo, sibling)).IsEqualTo(sibling);
    }
}
