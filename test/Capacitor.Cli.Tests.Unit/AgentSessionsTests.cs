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
    public async Task ExitedAt_reads_the_exit_record_time() {
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        Directory.CreateDirectory(Path.GetDirectoryName(ExitRecord("gone"))!);
        File.WriteAllText(ExitRecord("gone"), at.ToString("O", CultureInfo.InvariantCulture));

        await Assert.That(Sessions.ExitedAt(SessionId.Parse("gone")!)).IsEqualTo(at);
        await Assert.That(Sessions.ExitedAt(SessionId.Parse("elsewhere")!)).IsNull();
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
