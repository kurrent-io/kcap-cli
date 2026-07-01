using System.Globalization;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

/// <summary>
/// GitLab MR detection via `glab api` (raw JSON passthrough using glab's own auth —
/// kcap manages no token). Single-level owner/repo only.
/// </summary>
internal static class GitLabPrDetector {
    public static async Task<PrInfo?> DetectAsync(
            string host, string owner, string repo, string? branch, string cwd, TimeSpan cap, CommandRunner run) {
        // Guard: GitLab ignores an empty source_branch filter and returns ALL open MRs,
        // which would mis-tag the session (e.g. detached HEAD). No branch → no detection.
        if (string.IsNullOrEmpty(branch)) return null;

        var project = Uri.EscapeDataString($"{owner}/{repo}");
        var branchEnc = Uri.EscapeDataString(branch);
        var args = $"api --hostname {host} projects/{project}/merge_requests?source_branch={branchEnc}&state=opened";

        var json = await run("glab", args, cwd, cap);
        if (json is null) return null;

        try {
            if (JsonNode.Parse(json) is not JsonArray arr) return null;

            PrInfo? best = null;
            DateTimeOffset bestUpdated = DateTimeOffset.MinValue;

            foreach (var node in arr) {
                if (node is not JsonObject mr) continue;
                // Defense in depth: only accept an exact source_branch match.
                if (mr["source_branch"]?.GetValue<string>() != branch) continue;
                var iid = mr["iid"]?.GetValue<int>();
                if (iid is null) continue;

                var updated = ParseTimestamp(mr["updated_at"]?.GetValue<string>());
                if (best is null || updated > bestUpdated) {
                    bestUpdated = updated;
                    best = new PrInfo(iid.Value, mr["title"]?.GetValue<string>(),
                                      mr["web_url"]?.GetValue<string>(), mr["source_branch"]?.GetValue<string>());
                }
            }
            return best;
        } catch {
            return null; // best-effort
        }
    }

    static DateTimeOffset ParseTimestamp(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t : DateTimeOffset.MinValue;
}
