using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>Every way <see cref="AgentSessionIpc"/> cannot get an answer is no session, never a
/// failure: the MCP tool call it serves then falls back to the other session signals.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class AgentSessionIpcTests {
    const string DaemonName = "daemon-a";
    const string Session    = "ses_01JC7QY3VZ8K";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    Task<string?> Query() =>
        AgentSessionIpc.QueryAsync(Daemons.Store, DaemonName, "agent-7", TimeProvider.System, TimeSpan.FromSeconds(10));

    FakeAgentSessionDaemon Daemon(params string[] capabilities) =>
        new(Daemons.Store.SocketPath(DaemonName), capabilities, Session);

    [Test]
    public async Task No_daemon_listening_is_no_session() =>
        await Assert.That(await Query()).IsNull();

    /// <summary>An older daemon cannot decode the frame at all, so it is never sent one.</summary>
    [Test]
    public async Task A_daemon_that_does_not_advertise_the_frame_is_never_sent_it() {
        await using var daemon = Daemon("status/1");

        await Assert.That(await Query()).IsNull();
        await Assert.That(daemon.Received).IsEquivalentTo([FrameType.Hello]);
    }

    /// <summary>The positive control for the test above: the same fake does answer once it advertises
    /// the frame.</summary>
    [Test]
    public async Task A_daemon_that_advertises_the_frame_answers_the_session() {
        await using var daemon = Daemon(AgentSessionIpc.Capability);

        await Assert.That(await Query()).IsEqualTo(Session);
    }
}
