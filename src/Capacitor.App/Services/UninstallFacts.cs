using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Services;

/// What the confirmation needs to know; a null <see cref="Activity"/> with <see cref="DaemonRunning"/>
/// means a daemon is up but has not answered.
public sealed record UninstallFacts(
    string? LocalServerUrl,
    DaemonActivity? Activity,
    bool DaemonRunning,
    string? HostedServerUrl,
    IReadOnlyList<OtherKcapInstall> OtherInstalls);
