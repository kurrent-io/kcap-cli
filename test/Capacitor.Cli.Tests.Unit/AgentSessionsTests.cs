using System.Globalization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;

namespace Capacitor.Cli.Tests.Unit;

public class AgentSessionsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly int Agent = Environment.ProcessId;

    static readonly SessionId Session = SessionId.Parse("aaaa-1111")!;

    const int Hook = 900_003, Git = 900_002, Shell = 900_001;

    AgentSessions Sessions => field ??= new(Config.Root, pid => pid switch {
        Hook  => Git,
        Git   => Shell,
        Shell => Agent,
        _     => null,
    });

    string Note(int pid) => Config.Root.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A stale note on the way up names nothing.
    /// </summary>
    [Test]
    public async Task The_nearest_claimed_agent_above_names_the_session() {
        Sessions.Claim(Agent, Session);
        File.WriteAllText(Note(Shell), "stale\nlx:another-boot:1");

        await Assert.That(Sessions.Above(Hook)).IsEqualTo(Session);
        await Assert.That(Sessions.IsClaimed(Session)).IsTrue();
        await Assert.That(Sessions.IsClaimed(SessionId.Parse("stale")!)).IsFalse();
    }

    /// <summary>A note whose holder is gone names no live claimant, so a resumed session is seen only through
    /// the process that is actually running it.</summary>
    [Test]
    public async Task The_live_claimant_is_the_process_running_the_session() {
        DeadNote(Shell, Session.Value);

        await Assert.That(Sessions.LiveClaimant(Session)).IsNull();

        Sessions.Claim(Agent, Session);

        await Assert.That(Sessions.LiveClaimant(Session)).IsEqualTo(Agent);
    }

    [Test]
    public async Task Reap_drops_only_notes_no_live_process_holds() {
        Sessions.Claim(Agent, Session);
        File.WriteAllText(Note(Shell), "stale\nlx:another-boot:1");

        Sessions.Reap();

        await Assert.That(File.Exists(Note(Shell))).IsFalse();
        await Assert.That(Sessions.IsClaimed(Session)).IsTrue();
    }

    string ExitRecord(string session) => Config.Root.Path("agent-sessions", "exited", session);

    /// <summary>A note left by a process that is gone: the token names another boot.</summary>
    void DeadNote(int pid, string session) {
        Directory.CreateDirectory(Path.GetDirectoryName(Note(pid))!);
        File.WriteAllText(Note(pid), $"{session}\nlx:another-boot:1");
    }

    [Test]
    public async Task Reap_keeps_an_exit_record_for_a_dead_claim() {
        DeadNote(Shell, "gone");

        Sessions.Reap();

        await Assert.That(File.Exists(Note(Shell))).IsFalse();
        await Assert.That(Sessions.Liveness(SessionId.Parse("gone")!)).IsEqualTo(SessionLiveness.Exited);
    }

    [Test]
    public async Task Reap_treats_a_live_pid_under_another_start_token_as_reused() {
        var live   = ProcessStartToken.ForCurrent()!;
        var scheme = live[..live.IndexOf(':')];
        Directory.CreateDirectory(Path.GetDirectoryName(Note(Agent))!);
        File.WriteAllText(Note(Agent), $"reused\n{scheme}:another-boot:1");

        Sessions.Reap();

        await Assert.That(File.Exists(Note(Agent))).IsFalse();
        await Assert.That(Sessions.Liveness(SessionId.Parse("reused")!)).IsEqualTo(SessionLiveness.Exited);
    }

    /// <summary>The vendor and server a claim recorded come back with its session once the process is gone,
    /// so its end can be sent where the session lives.</summary>
    [Test]
    public async Task A_reaped_claim_returns_where_its_session_was_recorded() {
        DeadNote(Shell, "traced");
        File.WriteAllText(Note(Shell) + ".origin", "lx:another-boot:1\nclaude\nhttps://kcap.example");

        var exited = Sessions.ReapExited();

        await Assert.That(exited).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("traced")!, "claude", "https://kcap.example") });
        await Assert.That(File.Exists(Note(Shell) + ".origin")).IsFalse();
    }

    /// <summary>A claim writes its origin beside the note, bound to the claim's own start token.</summary>
    [Test]
    public async Task A_claim_writes_its_origin_under_its_own_token() {
        Sessions.Claim(Agent, Session, "claude", "https://kcap.example");

        await Assert.That(File.ReadAllText(Note(Agent) + ".origin"))
            .IsEqualTo($"{ProcessStartToken.ForPid(Agent)}\nclaude\nhttps://kcap.example");
    }

    /// <summary>A resumed session runs under a new process before the old claim is reaped; ending it then
    /// would close a running session.</summary>
    [Test]
    public async Task A_session_another_live_process_claims_is_not_returned() {
        Sessions.Claim(Agent, SessionId.Parse("resumed")!, "claude", "https://kcap.example");
        DeadNote(Shell, "resumed");

        await Assert.That(Sessions.ReapExited()).IsEmpty();
    }

    /// <summary>A reused pid's new claim writes its own origin; the reaped claim must not take it.</summary>
    [Test]
    public async Task An_origin_from_another_claim_of_the_pid_is_not_used_or_deleted() {
        DeadNote(Shell, "old");
        var origin = Note(Shell) + ".origin";
        File.WriteAllText(origin, "lx:new-boot:2\nclaude\nhttps://new.example");

        await Assert.That(Sessions.ReapExited()).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("old")!, null, null) });
        await Assert.That(File.Exists(origin)).IsTrue();
    }

    [Test]
    public async Task A_dead_claim_without_an_origin_returns_its_session_alone() {
        DeadNote(Shell, "bare");

        await Assert.That(Sessions.ReapExited()).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("bare")!, null, null) });
    }

    void UncomparableNote(string session) {
        Directory.CreateDirectory(Path.GetDirectoryName(Note(Agent))!);
        File.WriteAllText(Note(Agent), $"{session}\nlegacy-token-without-scheme");
    }

    /// <summary>A note on a live pid whose token cannot be compared may be a live claimant.</summary>
    [Test]
    public async Task Reap_leaves_a_note_it_cannot_compare_with_its_live_holder() {
        UncomparableNote("unclear");

        Sessions.Reap();

        await Assert.That(File.Exists(Note(Agent))).IsTrue();
        await Assert.That(Sessions.Liveness(SessionId.Parse("unclear")!)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task A_possibly_live_claimant_outweighs_an_exit_record() {
        DeadNote(Shell, Session.Value);
        Sessions.Reap();
        UncomparableNote(Session.Value);

        await Assert.That(Sessions.Liveness(Session)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task A_new_claim_deletes_the_sessions_exit_record() {
        DeadNote(Shell, Session.Value);
        Sessions.Reap();
        await Assert.That(File.Exists(ExitRecord(Session.Value))).IsTrue();

        Sessions.Claim(Agent, Session);

        await Assert.That(File.Exists(ExitRecord(Session.Value))).IsFalse();
        await Assert.That(Sessions.Liveness(Session)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task A_claim_in_another_case_is_running_for_the_canonical_id() {
        Sessions.Claim(Agent, SessionId.Parse("ABCD-EF01")!);

        await Assert.That(Sessions.Liveness(SessionId.Parse("abcdef01")!)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task An_exit_recorded_in_another_case_is_found_for_the_canonical_id() {
        DeadNote(Shell, "ABCD-EF01");
        Sessions.Reap();

        await Assert.That(Sessions.Liveness(SessionId.Parse("abcdef01")!)).IsEqualTo(SessionLiveness.Exited);
    }

    [Test]
    [Arguments(false, null, true)]
    [Arguments(true, false, true)]
    [Arguments(true, null, false)]
    [Arguments(true, true, false)]
    public async Task A_holder_is_gone_only_when_absent_or_reused(bool exists, bool? matches, bool gone) {
        await Assert.That(AgentSessions.HolderIsGone(exists, matches)).IsEqualTo(gone);
    }

    [Test]
    public async Task A_live_claim_is_running_even_with_an_exit_record() {
        DeadNote(Shell, Session.Value);
        Sessions.Reap();
        Sessions.Claim(Agent, Session);

        await Assert.That(Sessions.Liveness(Session)).IsEqualTo(SessionLiveness.Running);
    }

    [Test]
    public async Task A_session_nothing_here_ran_is_unknown() {
        await Assert.That(Sessions.Liveness(SessionId.Parse("elsewhere")!)).IsEqualTo(SessionLiveness.Unknown);
    }

    [Test]
    public async Task Reap_prunes_exit_records_past_retention() {
        Directory.CreateDirectory(Path.GetDirectoryName(ExitRecord("old"))!);
        File.WriteAllText(ExitRecord("old"), DateTimeOffset.UtcNow.AddDays(-31).ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(ExitRecord("recent"), DateTimeOffset.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(ExitRecord("garbled"), "not a time");

        Sessions.Reap();

        await Assert.That(File.Exists(ExitRecord("old"))).IsFalse();
        await Assert.That(File.Exists(ExitRecord("garbled"))).IsFalse();
        await Assert.That(File.Exists(ExitRecord("recent"))).IsTrue();
    }

    [Test]
    public async Task Exit_records_are_not_read_as_claims() {
        DeadNote(Shell, "gone");
        Sessions.Reap();
        Sessions.Claim(Agent, Session);

        await Assert.That(Sessions.Above(Hook)).IsEqualTo(Session);
        await Assert.That(Sessions.IsClaimed(SessionId.Parse("gone")!)).IsFalse();
    }
}
