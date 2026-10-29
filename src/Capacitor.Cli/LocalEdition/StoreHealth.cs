namespace Capacitor.Cli.LocalEdition;

/// <param name="Status">ASP.NET Core's health status: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</param>
sealed record StoreHealth(string Status, string? Description) {
    public bool Up => Status == "Healthy";
}
