namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What a server URL is, measured against the installation registry.</summary>
public abstract record LocalEndpointClass {
    LocalEndpointClass() { }

    /// <summary>Not a local installation's origin: any server, handled as today.</summary>
    public sealed record Ordinary : LocalEndpointClass;

    /// <summary>The active installation's origin, under any of its loopback spellings.</summary>
    public sealed record Local(LocalInstallation Installation) : LocalEndpointClass;

    /// <summary>A purged installation's origin. Whatever now listens on its port is not that server.</summary>
    public sealed record Retired(RetiredInstallation Installation) : LocalEndpointClass;

    /// <summary>A loopback URL while the registry cannot be read: it may be an installation, so it is
    /// not evidence that the URL is ordinary.</summary>
    public sealed record Undetermined(string Reason) : LocalEndpointClass;
}
