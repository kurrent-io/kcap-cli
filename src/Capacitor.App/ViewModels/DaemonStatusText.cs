using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// Settings' words for the daemon, kept apart: whether its process runs and the app is attached to it, and
/// separately whether it is connected to its server. Running is never read as connected, nor the reverse.
public static class DaemonStatusText {
    public static string Attach(AttachStatus status, DaemonInfoDto? daemon) => status.State switch {
        AttachState.Connected  => daemon?.Name is { Length: > 0 } name ? $"running · {name}" : "running",
        AttachState.Connecting => "connecting",
        _ => status.Reason switch {
            "daemon_unreachable"         => "not running",
            "daemon_incompatible"        => "version doesn't match the app",
            ExpectedDaemonRestart.Reason => "restarting",
            _                            => "unreachable",
        },
    };

    public static string? AttachHint(AttachStatus status) => status.State switch {
        AttachState.Unreachable => status.Reason switch {
            "daemon_unreachable"         => "Start it from the main window, or run kcap daemon start.",
            "daemon_incompatible"        => "Update kcap so the app and the daemon are the same version.",
            ExpectedDaemonRestart.Reason => null,
            _                            => "Reconnect from the main window. If it stays unreachable, restart the daemon.",
        },
        _ => null,
    };

    /// The daemon's own connection word, its sign-in named when that is what keeps it off the server. Null while not
    /// attached: a snapshot kept from before says nothing about a daemon the app cannot reach.
    public static string? Connection(AttachStatus status, DaemonInfoDto? daemon) =>
        status.State != AttachState.Connected || daemon is null ? null
        : daemon.Connection != "connected" && daemon.Credential is { } credential
            ? credential == "rejected" ? "sign-in rejected" : "signed out"
        : daemon.Connection;

    public static string? Server(AttachStatus status, DaemonInfoDto? daemon) =>
        Connection(status, daemon) is not { } word ? null
        : string.IsNullOrEmpty(daemon!.ServerUrl) ? word
        : $"{word} · {daemon.ServerUrl}";

    public static string? ServerHint(AttachStatus status, DaemonInfoDto? daemon) => Connection(status, daemon) switch {
        "signed out" or "sign-in rejected" => "Sign in to the daemon's profile under Profiles.",
        "disconnected" or "reconnecting"   => "The daemon keeps retrying. Check that the server is running and reachable.",
        _                                  => null,
    };
}
