using System.Text.Json.Nodes;

namespace Capacitor.Cli.PrDetection;

/// <summary>Spawns a CLI and returns trimmed stdout, or null on failure/timeout.</summary>
internal delegate Task<string?> CommandRunner(string cmd, string arguments, string cwd, TimeSpan timeout);

internal sealed record PrInfo(int Number, string? Title, string? Url, string? HeadRef);

/// <summary>GitHub / GitHub Enterprise detection via `gh` (auto-targets the remote's host).</summary>
internal static class GitHubPrDetector {
    public static async Task<PrInfo?> DetectAsync(string cwd, TimeSpan cap, CommandRunner run) {
        var json = await run("gh", "pr view --json number,title,url,headRefName", cwd, cap);
        if (json is null) return null;
        try {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            var number = o["number"]?.GetValue<int>();
            if (number is null) return null;
            return new PrInfo(number.Value, o["title"]?.GetValue<string>(),
                              o["url"]?.GetValue<string>(), o["headRefName"]?.GetValue<string>());
        } catch {
            return null; // best-effort
        }
    }
}
