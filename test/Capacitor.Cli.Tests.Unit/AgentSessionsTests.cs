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

    static readonly ClaimOrigin Here = new("claude", "https://kcap.example", "/t/x.jsonl", "/repo");

    void DeadNoteWithOrigin(int pid, string session, string server) {
        DeadNote(pid, session);
        File.WriteAllText(Note(pid) + ".origin", $"lx:another-boot:1\nclaude\n{server}\n/t/x.jsonl\n/repo");
    }

    /// <summary>Where a claim recorded its session comes back once the process is gone, so its end can be
    /// sent where the session lives.</summary>
    [Test]
    public async Task A_reaped_claim_returns_where_its_session_was_recorded() {
        DeadNoteWithOrigin(Shell, "traced", "https://kcap.example");

        var exited = Sessions.ReapExited("https://kcap.example/");

        await Assert.That(exited).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("traced")!, Here) });
        await Assert.That(File.Exists(Note(Shell) + ".origin")).IsFalse();
        await Assert.That(File.Exists(Note(Shell))).IsFalse();
    }

    /// <summary>An end for a session recorded against another server is set aside per session, so a later
    /// process reusing the pid cannot erase it, and a pass on that server takes it.</summary>
    [Test]
    public async Task An_end_for_another_server_waits_for_that_server_whatever_reuses_the_pid() {
        DeadNoteWithOrigin(Shell, "away", "https://other.example");

        await Assert.That(Sessions.ReapExited("https://kcap.example")).IsEmpty();
        await Assert.That(File.Exists(Note(Shell))).IsFalse().Because("the pid is free for the next claim");
        await Assert.That(Sessions.Liveness(SessionId.Parse("away")!)).IsEqualTo(SessionLiveness.Exited);

        DeadNoteWithOrigin(Shell, "next", "https://kcap.example");
        await Assert.That(Sessions.ReapExited("https://kcap.example").Select(e => e.Session.Value)).IsEquivalentTo(new[] { "next" });

        var taken = Sessions.ReapExited("https://other.example/");
        await Assert.That(taken).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("away")!, Here with { Server = "https://other.example" }) });
        await Assert.That(Sessions.ReapExited("https://other.example")).IsEmpty().Because("taken once");
    }

    /// <summary>A session whose end was set aside, then resumed and gone again, is ended once.</summary>
    [Test]
    public async Task A_deferred_end_and_a_fresh_reap_of_the_same_session_end_it_once() {
        DeadNoteWithOrigin(Shell, "twice", "https://other.example");
        Sessions.ReapExited("https://kcap.example");
        DeadNoteWithOrigin(Git, "twice", "https://other.example");

        await Assert.That(Sessions.ReapExited("https://other.example").Select(e => e.Session.Value)).IsEquivalentTo(new[] { "twice" });
    }

    /// <summary>A claim writes its origin under its own start token; a repeat claim keeps the first one.</summary>
    [Test]
    public async Task A_repeat_claim_keeps_the_first_origin() {
        Sessions.Claim(Agent, Session, Here);
        Sessions.Claim(Agent, Session, new ClaimOrigin("claude", null, null, null));

        await Assert.That(AgentOrigin()).IsEqualTo(Here);
    }

    ClaimOrigin? AgentOrigin() => Sessions.ReadOrigin(Agent, ProcessStartToken.ForPid(Agent)!);

    /// <summary>A claim made before the server was known leaves its end nowhere to go, so a later claim
    /// that knows the server fills it in.</summary>
    /// <remarks>What the first claim recorded and the later one leaves out is kept.</remarks>
    [Test]
    public async Task A_repeat_claim_fills_in_a_server_the_first_lacked() {
        Sessions.Claim(Agent, Session, Here with { Server = null });
        Sessions.Claim(Agent, Session, new ClaimOrigin(null, "https://kcap.example", null, null));

        await Assert.That(AgentOrigin()).IsEqualTo(Here);
    }

    /// <summary>A claim that knows the server but not yet the transcript or folder leaves those for a later
    /// claim to fill, as a watcher armed after the first hook does.</summary>
    [Test]
    public async Task A_repeat_claim_fills_in_a_path_the_first_lacked() {
        Sessions.Claim(Agent, Session, Here with { TranscriptPath = null, Cwd = null });
        Sessions.Claim(Agent, Session, Here with { Server = "https://later.example" });

        await Assert.That(AgentOrigin()).IsEqualTo(Here);
    }

    /// <summary>The hosted agent and daemon epoch a claim ran under come back with its origin, through a
    /// deferred end too, so the end can be fenced against a resume of that agent.</summary>
    [Test]
    public async Task The_hosted_agent_and_epoch_come_back_with_the_origin() {
        var hosted = Here with { AgentId = "agent-1", DaemonEpoch = "epoch-1" };
        Sessions.Claim(Agent, Session, hosted);

        await Assert.That(AgentOrigin()).IsEqualTo(hosted);
    }

    /// <summary>A path may hold a newline on Unix; the origin still reads back whole.</summary>
    [Test]
    public async Task An_origin_with_a_newline_in_a_path_reads_back_whole() {
        var origin = Here with { Cwd = "/re\npo%41" };
        Sessions.Claim(Agent, Session, origin);

        await Assert.That(AgentOrigin()).IsEqualTo(origin);
    }

    /// <summary>A session id may hold a dot, which no unfinished write may be mistaken for.</summary>
    [Test]
    public async Task A_deferred_end_for_a_dotted_session_is_taken() {
        DeadNoteWithOrigin(Shell, "a.b", "https://other.example");
        Sessions.ReapExited("https://kcap.example");

        await Assert.That(Sessions.ReapExited("https://other.example").Select(e => e.Session.Value))
            .IsEquivalentTo(new[] { "a.b" });
    }

    /// <summary>A deferred end is filed under a case-folded name, but the server is told the id as claimed.</summary>
    [Test]
    public async Task A_deferred_end_keeps_the_sessions_case() {
        DeadNoteWithOrigin(Shell, "AwayMiXed", "https://other.example");
        Sessions.ReapExited("https://kcap.example");

        await Assert.That(Sessions.ReapExited("https://other.example").Select(e => e.Session.Value))
            .IsEquivalentTo(new[] { "AwayMiXed" });
    }

    /// <summary>Scheme and host fold case and a trailing slash is ignored, but a path names a tenant as written.</summary>
    [Test]
    [Arguments("https://KCAP.example/t/", "https://kcap.example/t", true)]
    [Arguments("https://kcap.example/Tenant", "https://kcap.example/tenant", false)]
    [Arguments("https://kcap.example", null, false)]
    public async Task Servers_match_as_server_identities(string a, string? b, bool same) =>
        await Assert.That(AgentSessions.SameServer(a, b)).IsEqualTo(same);

    /// <summary>A resumed session runs under a new process before the old claim is reaped; ending it then
    /// would close a running session.</summary>
    [Test]
    public async Task A_session_another_live_process_claims_is_not_returned() {
        Sessions.Claim(Agent, SessionId.Parse("resumed")!, Here);
        DeadNoteWithOrigin(Shell, "resumed", "https://kcap.example");

        await Assert.That(Sessions.ReapExited("https://kcap.example")).IsEmpty();
    }

    /// <summary>A reused pid's new claim writes its own origin; the reaped claim must not take it.</summary>
    [Test]
    public async Task An_origin_from_another_claim_of_the_pid_is_not_used_or_deleted() {
        DeadNote(Shell, "old");
        var origin = Note(Shell) + ".origin";
        File.WriteAllText(origin, "lx:new-boot:2\nclaude\nhttps://new.example\n\n");

        await Assert.That(Sessions.ReapExited("https://new.example")).IsEquivalentTo(new[] { new ExitedSession(SessionId.Parse("old")!, null) });
        await Assert.That(File.Exists(origin)).IsTrue();
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
