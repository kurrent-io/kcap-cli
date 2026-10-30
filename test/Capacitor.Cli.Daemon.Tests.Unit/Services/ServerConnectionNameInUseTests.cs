using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ServerConnectionNameInUseTests {
    /// <summary>
    /// What the client receives when the server hub rejects <c>DaemonConnect</c>: SignalR's dispatcher
    /// wraps the server's message, so the sentinel is never at the start.
    /// </summary>
    public const string WrappedRejection =
        "An unexpected error occurred invoking 'DaemonConnect' on the server. HubException: DAEMON_NAME_IN_USE: daemon 'refusal-name' is already registered by another live instance for this account. Pick a different --name, or stop the other instance (run `kcap daemon stop --name refusal-name` on the machine where it's running).";

    [Test]
    public async Task WrappedServerRejection_IsNameInUse() =>
        await Assert.That(ServerConnection.IsNameInUse(new HubException(WrappedRejection))).IsTrue();

    [Test]
    public async Task BareSentinelMessage_IsNameInUse() =>
        await Assert.That(ServerConnection.IsNameInUse(new HubException("DAEMON_NAME_IN_USE: daemon 'x' is taken"))).IsTrue();

    [Test]
    public async Task OtherHubException_IsNotNameInUse() =>
        await Assert.That(ServerConnection.IsNameInUse(new HubException("An unexpected error occurred invoking 'DaemonConnect' on the server."))).IsFalse();

    [Test]
    public async Task SentinelOutsideHubException_IsNotNameInUse() =>
        await Assert.That(ServerConnection.IsNameInUse(new InvalidOperationException(WrappedRejection))).IsFalse();
}
