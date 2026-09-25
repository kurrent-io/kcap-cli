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
}
