using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>Compares the tool set the reviewer extension reported with the one the launch was built
/// from. Pi drops an allowlisted name that matches nothing without a diagnostic, so agreement here is
/// the only evidence the reviewer can report at all.</summary>
internal static class PiReviewerReadiness {
    internal static string? Verify(string readyPath, IReadOnlyList<PiReviewerTool> expected) {
        HashSet<string> active;

        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(readyPath));
            active = [.. doc.RootElement.GetProperty("active").EnumerateArray().Select(e => e.GetString() ?? "")];
        } catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException) {
            return "pi_reviewer_tool_surface_mismatch: the reviewer extension produced no readable readiness report.";
        }

        var wanted  = expected.Select(t => t.PiName).ToHashSet(StringComparer.Ordinal);
        var missing = wanted.Except(active).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var extra   = active.Except(wanted).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        if (missing.Length == 0 && extra.Length == 0) return null;

        return "pi_reviewer_tool_surface_mismatch: the reviewer's live tools differ from the launch's list"
             + (missing.Length > 0 ? $"; missing: {string.Join(", ", missing)}" : "")
             + (extra.Length   > 0 ? $"; unexpected: {string.Join(", ", extra)}" : "") + ".";
    }
}
