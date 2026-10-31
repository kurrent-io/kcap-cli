using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// The daemon's process and attach are worded apart from its server connection: a running daemon off its server
/// reads as running and disconnected, and a credential the daemon reports is named rather than read as an outage.
public class DaemonStatusTextTests {
    static readonly AttachStatus Attached = new(AttachState.Connected, null, null);

    static DaemonInfoDto Daemon(string connection, string? credential = null, string serverUrl = "http://localhost:9473") =>
        FakeDaemonClientService.Snap(daemon: "work-pc", serverUrl: serverUrl, connection: connection, credential: credential).Daemon;

    [Test]
    [Arguments(AttachState.Connecting, null, "connecting", null)]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "not running", "Start it from the main window, or run kcap daemon start.")]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "version doesn't match the app", "Update kcap so the app and the daemon are the same version.")]
    [Arguments(AttachState.Unreachable, ExpectedDaemonRestart.Reason, "restarting", null)]
    public async Task A_daemon_that_is_not_attached_names_its_state_and_has_no_server_line(
            AttachState state, string? reason, string text, string? hint) {
        var status = new AttachStatus(state, reason, null);

        await Assert.That(DaemonStatusText.Attach(status, Daemon("connected"))).IsEqualTo(text);
        await Assert.That(DaemonStatusText.AttachHint(status)).IsEqualTo(hint);
        await Assert.That(DaemonStatusText.Server(status, Daemon("connected"))).IsNull();
    }

    [Test]
    [Arguments("connected", null, "connected · http://localhost:9473", null)]
    [Arguments("reconnecting", null, "reconnecting · http://localhost:9473", "The daemon keeps retrying. Check that the server is running and reachable.")]
    [Arguments("disconnected", "signed_out", "signed out · http://localhost:9473", "Sign in to the daemon's profile under Profiles.")]
    [Arguments("disconnected", "rejected", "sign-in rejected · http://localhost:9473", "Sign in to the daemon's profile under Profiles.")]
    public async Task An_attached_daemon_is_running_whatever_its_server_connection(
            string connection, string? credential, string server, string? hint) {
        var daemon = Daemon(connection, credential);

        await Assert.That(DaemonStatusText.Attach(Attached, daemon)).IsEqualTo("running · work-pc");
        await Assert.That(DaemonStatusText.Server(Attached, daemon)).IsEqualTo(server);
        await Assert.That(DaemonStatusText.ServerHint(Attached, daemon)).IsEqualTo(hint);
    }
}
