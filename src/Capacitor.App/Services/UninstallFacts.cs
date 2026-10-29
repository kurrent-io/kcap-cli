using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Services;

/// What the confirmation needs to know. <see cref="Activity"/> is the attached daemon's, null while it
/// is not attached; <see cref="LiveDaemons"/> holds every daemon whose pid is live, attached included.
/// A null list could not be read, which is not the same as an empty one.
public sealed record UninstallFacts(
    string? LocalServerUrl,
    string AttachedDaemon,
    DaemonActivity? Activity,
    IReadOnlyList<string>? LiveDaemons,
    string? HostedServerUrl,
    IReadOnlyList<OtherKcapInstall>? OtherInstalls,
    IReadOnlyList<string>? PathProfiles = null);
