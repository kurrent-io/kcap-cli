using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class AgentRowTests {
    static RepoSessionDto Listed(
            string id = "s1", string status = "ended", string? repoOwner = "kurrent-io", string? repoName = "kcap-cli",
            string? cwd = "/Users/alexey/dev/kcap-cli", bool stale = false) => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = "Desktop app RAM growth",
        Owner = new() { UserId = "user_01", Username = "alexey", DisplayName = "Alexey Zimarev", AvatarUrl = "https://avatars.example/user_01" },
        StartedAt = new DateTimeOffset(2026, 10, 22, 13, 4, 24, TimeSpan.Zero),
        EndedAt = status == "ended" ? new DateTimeOffset(2026, 10, 22, 16, 56, 41, TimeSpan.Zero) : null,
        LastActivityAt = new DateTimeOffset(2026, 10, 22, 16, 56, 41, TimeSpan.Zero),
        Cwd = cwd, Stale = stale,
        Repo = repoOwner is null ? null : new() { Hash = "830e1690b97dd8c0", Owner = repoOwner, Name = repoName },
    };

    [Test]
    public async Task Server_row_carries_the_recording_identity() {
        var recording = new RepoSessionRecordingDto { MachineId = "m-1", DaemonName = "laptop", AccountId = "acct-1", ConfigDir = "/Users/me/.claude" };

        var row = AgentRow.FromServer(Listed() with { Recording = recording }, hostedPresentation: false);

        await Assert.That(row.Recording).IsEqualTo(recording);
        await Assert.That(row.RecordedOn("m-1")).IsTrue();
        await Assert.That(row.RecordedOn("m-2")).IsFalse();
    }

    /// A listing from an older server, or for a session recorded before its CLI reported it, names no
    /// machine; neither it nor an app with no machine id of its own may read as this machine.
    [Test]
    public async Task A_missing_machine_id_is_never_this_machine() {
        var unrecorded = AgentRow.FromServer(Listed(), hostedPresentation: false);
        var noMachine  = AgentRow.FromServer(Listed() with { Recording = new() { ConfigDir = "/Users/me/.claude" } }, hostedPresentation: false);
        var recorded   = AgentRow.FromServer(Listed() with { Recording = new() { MachineId = "m-1" } }, hostedPresentation: false);

        await Assert.That(unrecorded.RecordedOn("m-1")).IsFalse();
        await Assert.That(noMachine.RecordedOn("m-1")).IsFalse();
        await Assert.That(recorded.RecordedOn(null)).IsFalse();
        await Assert.That(recorded.RecordedOn("")).IsFalse();
    }

    [Test]
    public async Task Server_row_maps_an_ended_listing_row() {
        var row = AgentRow.FromServer(Listed(), hostedPresentation: false);

        await Assert.That(row.Key).IsEqualTo("server:s1");
        await Assert.That(row.Origin).IsEqualTo(AgentOrigin.Server);
        await Assert.That(row.Id).IsEqualTo("s1");
        await Assert.That(row.SessionId).IsEqualTo("s1");
        await Assert.That(row.Status).IsEqualTo("Completed");
        await Assert.That(row.Vendor).IsEqualTo("claude");
        await Assert.That(row.RepoGroupKey).IsEqualTo("repo:kurrent-io/kcap-cli");
        await Assert.That(row.RepoGroupLabel).IsEqualTo("kurrent-io/kcap-cli");
        await Assert.That(row.OwnerUserId).IsEqualTo("user_01");
        await Assert.That(row.OwnerName).IsEqualTo("Alexey Zimarev");
        await Assert.That(row.OwnerAvatarUrl).IsEqualTo("https://avatars.example/user_01");
        await Assert.That(row.CreatedAt).IsEqualTo(new DateTime(2026, 10, 22, 13, 4, 24, DateTimeKind.Utc));
        await Assert.That(row.EndedAt).IsEqualTo(new DateTime(2026, 10, 22, 16, 56, 41, DateTimeKind.Utc));
        await Assert.That(row.LastActivityAt).IsEqualTo(new DateTime(2026, 10, 22, 16, 56, 41, DateTimeKind.Utc));
        await Assert.That(row.IsEndedSession).IsTrue();
        await Assert.That(row.IsTerminalSession).IsFalse();
        await Assert.That(row.MachineBadge).IsNull();
    }

    [Test]
    public async Task Server_row_without_repo_or_cwd_groups_under_no_repository() {
        var row = AgentRow.FromServer(Listed(repoOwner: null, cwd: null), hostedPresentation: false);

        await Assert.That(row.RepoGroupLabel).IsEqualTo("No repository");
        await Assert.That(row.RepoGroupKey).IsEqualTo("daemon:server:");
        await Assert.That(row.RepoPath).IsNull();
    }

    [Test]
    public async Task Live_server_row_is_a_terminal_session_unless_hosted() {
        var terminal = AgentRow.FromServer(Listed(status: "active", stale: true), hostedPresentation: false);
        var hosted = AgentRow.FromServer(Listed(status: "active"), hostedPresentation: true);

        await Assert.That(terminal.Status).IsEqualTo("Running");
        await Assert.That(terminal.IsTerminalSession).IsTrue();
        await Assert.That(terminal.IsStale).IsTrue();
        await Assert.That(terminal.IsEndedSession).IsFalse();
        await Assert.That(terminal.EndedAt).IsNull();
        await Assert.That(hosted.IsTerminalSession).IsFalse();
    }

    [Test]
    public async Task Remote_row_carries_the_registry_owner() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "a1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "user_02", Vendor = "codex",
        });

        await Assert.That(row.OwnerUserId).IsEqualTo("user_02");
        await Assert.That(row.IsEndedSession).IsFalse();
    }

    static AgentInstanceDto Remote(string? title, string? prompt = "fix the flaky test\nmore") => new() {
        AgentId = "r1", SessionId = "s2", Status = "Running", DaemonName = "d",
        OwnerUserId = "u7", RegisteredAt = DateTime.UtcNow, Title = title, Prompt = prompt,
    };

    [Test]
    public async Task FromRemote_prefers_the_server_title() {
        var row = AgentRow.FromRemote(Remote("Renamed on the server"));
        await Assert.That(row.Title).IsEqualTo("Renamed on the server");
    }

    [Test]
    public async Task FromRemote_falls_back_to_the_prompt_cut() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).Title).IsEqualTo("fix the flaky test");
        await Assert.That(AgentRow.FromRemote(Remote("")).Title).IsEqualTo("fix the flaky test");
    }

    [Test]
    public async Task FromRemote_carries_the_owner() {
        await Assert.That(AgentRow.FromRemote(Remote(null)).OwnerUserId).IsEqualTo("u7");
    }
}
