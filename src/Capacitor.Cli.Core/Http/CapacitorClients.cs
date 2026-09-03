namespace Capacitor.Cli.Core.Http;

/// <summary>Named clients registered by <see cref="CapacitorHttpServices.AddCapacitorHttp"/>.</summary>
public static class CapacitorClients {
    /// <summary>Authenticated against our own server; redirects are not followed.</summary>
    public const string Default = "capacitor";
}
