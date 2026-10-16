namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The durable record of the machine's local installation: the active one, if any, and every one
/// that was purged. Both live in one document so a purge moves an identity from one to the other in a
/// single replacement, and no reader sees a moment when an origin is neither.
/// </summary>
public sealed record InstallRegistry(LocalInstallation? Active, IReadOnlyList<RetiredInstallation> Retired) {
    public static readonly InstallRegistry Empty = new(null, []);

    public bool IsRetiredPort(int port) => Retired.Any(r => r.Port == port);

    public InstallRegistry WithActive(LocalInstallation installation) {
        if (IsRetiredPort(installation.Port))
            throw new ArgumentException($"Port {installation.Port} belonged to a purged installation and cannot be reused.", nameof(installation));

        return this with { Active = installation };
    }

    /// <summary>Moves the active installation to the retired list.</summary>
    public InstallRegistry RetireActive() =>
        Active is { } active
            ? new(null, [.. Retired, new RetiredInstallation(active.Id, active.Port, active.ServerUrl)])
            : this;
}
