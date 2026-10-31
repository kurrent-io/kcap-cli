using System.Text.Json;

namespace Capacitor.App.Services;

/// <param name="UnmatchedFolders">Empty, or null when built by hand, from a CLI that does not list them.</param>
public sealed record ImportDiscoveryReport(
    IReadOnlyList<ImportDiscoveryRepo>   Repos,
    int                                  UnmatchedSessions,
    IReadOnlyList<ImportDiscoveryWindow> Windows,
    IReadOnlyList<ImportDiscoveryFolder>? UnmatchedFolders = null) {
    /// Null for output that is not the report — an older CLI, or a crash mid-write.
    public static ImportDiscoveryReport? Parse(string json) {
        try {
            var report = JsonSerializer.Deserialize(json, ImportDiscoveryReportJson.Default.ImportDiscoveryReport);
            if (report?.Repos is null || report.UnmatchedSessions < 0 ||
                report.Repos.Any(r => r is null || string.IsNullOrWhiteSpace(r.Owner) ||
                    string.IsNullOrWhiteSpace(r.Name) || r.Sessions < 0 ||
                    Invalid(r.Windows) || Invalid(r.Vendors)) ||
                report.UnmatchedFolders?.Any(f => f is null || f.Sessions < 0 || f.Folder?.Length == 0 ||
                    Invalid(f.Windows) || Invalid(f.Vendors)) == true ||
                Invalid(report.Windows)) return null;
            return report with { Windows = report.Windows ?? [], UnmatchedFolders = report.UnmatchedFolders ?? [] };
        } catch (JsonException) {
            return null;
        }
    }

    static bool Invalid(IReadOnlyList<ImportDiscoveryWindow>? windows) => windows?.Any(w => w is null || w.Sessions < 0) == true;

    static bool Invalid(IReadOnlyList<ImportDiscoveryVendor>? vendors) =>
        vendors?.Any(v => v is null || string.IsNullOrWhiteSpace(v.Vendor) || v.Sessions < 0 || Invalid(v.Windows)) == true;
}
