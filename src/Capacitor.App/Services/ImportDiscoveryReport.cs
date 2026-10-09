using System.Text.Json;

namespace Capacitor.App.Services;

public sealed record ImportDiscoveryReport(
    IReadOnlyList<ImportDiscoveryRepo>   Repos,
    int                                  UnmatchedSessions,
    IReadOnlyList<ImportDiscoveryWindow> Windows) {
    /// Null for output that is not the report — an older CLI, or a crash mid-write.
    public static ImportDiscoveryReport? Parse(string json) {
        try {
            var report = JsonSerializer.Deserialize(json, ImportDiscoveryReportJson.Default.ImportDiscoveryReport);
            if (report?.Repos is null || report.UnmatchedSessions < 0 ||
                report.Repos.Any(r => r is null || string.IsNullOrWhiteSpace(r.Owner) ||
                    string.IsNullOrWhiteSpace(r.Name) || r.Sessions < 0 ||
                    r.Windows?.Any(w => w is null || w.Sessions < 0) == true) ||
                report.Windows?.Any(w => w is null || w.Sessions < 0) == true) return null;
            return report with { Windows = report.Windows ?? [] };
        } catch (JsonException) {
            return null;
        }
    }
}
