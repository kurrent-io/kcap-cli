namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>What the host reported in its <c>initialize</c> result.</summary>
internal sealed record MuseHostHandshake(
    int                  SchemaVersion,
    string?              Fingerprint,
    IReadOnlySet<string> GrantedCapabilities,
    string?              SandboxAvailability
);
