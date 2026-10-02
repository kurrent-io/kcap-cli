using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// <summary>What <c>kcap import --discover --json</c> reports: history per repository and per
/// <c>--since</c> window, read off this machine's disk before anything is uploaded.</summary>
public sealed record ImportDiscoveryReport(
    IReadOnlyList<ImportDiscoveryRepo>   Repos,
    int                                  UnmatchedSessions,
    IReadOnlyList<ImportDiscoveryWindow> Windows) {
    /// Null for output that is not the report — an older CLI, or a crash mid-write.
    public static ImportDiscoveryReport? Parse(string json) {
        try {
            return JsonSerializer.Deserialize(json, ImportDiscoveryReportJson.Default.ImportDiscoveryReport);
        } catch (JsonException) {
            return null;
        }
    }
}

/// <param name="Windows">This repository's sessions per window, in the report's own window order. Empty
/// from a CLI that predates the field.</param>
public sealed record ImportDiscoveryRepo(
    string Owner, string Name, int Sessions, DateTimeOffset? LastSessionAt, IReadOnlyList<ImportDiscoveryWindow>? Windows) {
    public string Slug => $"{Owner}/{Name}";
}

/// <param name="Since">The window's first day, or null for everything.</param>
public sealed record ImportDiscoveryWindow(DateOnly? Since, int Sessions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ImportDiscoveryReport))]
internal partial class ImportDiscoveryReportJson : JsonSerializerContext;
