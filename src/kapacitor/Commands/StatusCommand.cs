using System.Text.Json.Nodes;
using kapacitor.Auth;

namespace kapacitor.Commands;

public static class StatusCommand {
    public static async Task<int> HandleAsync(string? baseUrl) {
        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            await Console.Out.WriteLineAsync("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                // ReSharper disable once ShortLivedHttpClient
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(5);
                var resp = await http.GetAsync($"{baseUrl}/auth/config");
                await Console.Out.WriteLineAsync(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                await Console.Out.WriteLineAsync("✗ unreachable");
            }
        }

        // Auth
        Console.Write("  Auth:    ");
        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is not null) {
            var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;

            var expiryText = remaining.TotalHours > 1
                ? $"expires in {remaining.TotalHours:F0}h"
                : $"expires in {remaining.TotalMinutes:F0}m";
            await Console.Out.WriteLineAsync($"{tokens.GitHubUsername} ({tokens.Provider}) ✓ token valid ({expiryText})");
        } else {
            var rawTokens = await TokenStore.LoadAsync();

            await Console.Out.WriteLineAsync(rawTokens is not null
                ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired (run: kapacitor login)"
                : "not authenticated (run: kapacitor login)");
        }

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var claudeInstalled = IsClaudePluginInstalled(ClaudePaths.UserSettings);
        var codexInstalled  = IsCodexHooksInstalled(CodexPaths.UserHooksJson);

        var parts = new List<string>();
        parts.Add(claudeInstalled ? "Claude ✓" : "Claude ✗");
        parts.Add(codexInstalled ? "Codex ✓" : "Codex ✗");

        await Console.Out.WriteLineAsync(string.Join("  ", parts));

        // Agent
        Console.Write("  Agent:   ");

        var pidPath = PathHelpers.ConfigPath("agent.pid");

        if (File.Exists(pidPath)) {
            // The PID file is one or two lines: PID, optionally followed by
            // process StartTicks (UTC ticks of Process.StartTime). Parse only
            // the first non-empty line as the PID — naively passing the whole
            // contents to int.TryParse fails on the two-line format and
            // mis-reports a running daemon as "invalid PID file".
            var firstLine = (await File.ReadAllTextAsync(pidPath))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (int.TryParse(firstLine, out var pid)) {
                try {
                    System.Diagnostics.Process.GetProcessById(pid);
                    await Console.Out.WriteLineAsync($"running (PID {pid})");
                } catch (ArgumentException) {
                    await Console.Out.WriteLineAsync("not running (stale PID file)");
                }
            } else {
                await Console.Out.WriteLineAsync("unknown (invalid PID file)");
            }
        } else {
            await Console.Out.WriteLineAsync("not running");
        }

        return 0;
    }

    /// <summary>
    /// True iff <paramref name="settingsPath"/> exists and has
    /// <c>enabledPlugins["kapacitor@kapacitor"] == true</c>.
    /// </summary>
    public static bool IsClaudePluginInstalled(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
            if (root["enabledPlugins"] is not JsonObject enabled) return false;

            return enabled["kapacitor@kapacitor"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// True iff <paramref name="hooksPath"/> exists and any hook entry under any
    /// event references the <c>kapacitor codex-hook</c> command.
    /// </summary>
    public static bool IsCodexHooksInstalled(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, value) in hooks) {
                if (value is not JsonArray entries) continue;

                if (entries.Any(entry => CodexHooksParser.EntryReferencesKapacitorCodexHook(entry))) {
                    return true;
                }
            }

            return false;
        } catch {
            return false;
        }
    }
}
