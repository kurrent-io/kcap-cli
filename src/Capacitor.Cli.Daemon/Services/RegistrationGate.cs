using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Tracks whether the daemon's hub connection is ready to carry a daemon-scoped
/// invocation — i.e. the transport is <see cref="HubConnectionState.Connected"/>
/// AND <c>DaemonConnect</c> (re-registration) has completed on the current
/// connection. Used by the permission-request retry loop so a retry never fires
/// against a connection the server has not (re-)registered, which would surface
/// as a HubException and be mistaken for a final deny.
///
/// The flag is reset on every connection drop (Reconnecting/Closed) and at the
/// start of every re-registration (including the heartbeat slot-displacement
/// path, which re-registers without any transport-loss event), and set only
/// after a successful registration.
/// </summary>
internal sealed class RegistrationGate {
    volatile bool _registered;

    public void MarkUnregistered() => _registered = false;
    public void MarkRegistered()   => _registered = true;

    public bool IsReady(HubConnectionState state) =>
        state == HubConnectionState.Connected && _registered;
}
