namespace Capacitor.App.Services;

public sealed record ImportDiscoveryRepo(
    string Owner, string Name, int Sessions, DateTimeOffset? LastSessionAt, IReadOnlyList<ImportDiscoveryWindow>? Windows) {
    public string Slug => $"{Owner}/{Name}";
}
