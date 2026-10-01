using System.Globalization;
using Capacitor.Cli.Core;

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
