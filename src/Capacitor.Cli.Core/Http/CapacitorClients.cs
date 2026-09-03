namespace Capacitor.Cli.Core.Http;

/// <summary>Named clients registered by <see cref="CapacitorHttpServices.AddCapacitorHttp"/>.</summary>
public static class CapacitorClients {
    /// <summary>Authenticated against our own server; redirects are not followed.</summary>
    public const string Default = "capacitor";

    /// <summary>Carries no credential, so it follows redirects and cannot recover from a 401.</summary>
    public const string Anonymous = "capacitor-anonymous";

    /// <summary>A daemon-minted loopback capability: no credential, no proxy, no redirect.</summary>
    public const string Loopback = "capacitor-loopback";

    /// <summary>The caller's own bearer, sent exactly once: no rotation, no redirect.</summary>
    public const string Bearer = "capacitor-bearer";
}
