using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Auth;

namespace Kapacitor.Cli.Commands;

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

            await Console.Out.WriteLineAsync(
                rawTokens is not null
                    ? $"{rawTokens.GitHubUsername} ({rawTokens.Provider}) ✗ token expired (run: kapacitor login)"
                    : "not authenticated (run: kapacitor login)"
            );
        }

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var claudeInstalled = IsClaudePluginInstalled(ClaudePaths.UserSettings);
        var codexInstalled  = IsCodexHooksInstalled(CodexPaths.UserHooksJson);

        var parts = new List<string> {
            claudeInstalled ? "Claude ✓" : "Claude ✗",
            codexInstalled ? "Codex ✓" : "Codex ✗"
        };

        await Console.Out.WriteLineAsync(string.Join("  ", parts));

        // Daemon — AI-630: read per-name PID files under
        // ~/.config/kapacitor/daemons/ instead of the pre-AI-630 singleton
        // at ~/.config/kapacitor/agent.pid. The top-level `kapacitor status`
        // must agree with `kapacitor daemon status`; previously this
        // command kept saying "not running" while `daemon status` reported
        // a healthy daemon because new daemons no longer write the legacy
        // singleton.
        Console.Write("  Daemon:  ");
        await WriteAgentStatusAsync();

        return 0;
    }

    static async Task WriteAgentStatusAsync() {
        if (!Directory.Exists(DaemonLockPaths.Directory)) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var pidFiles = Directory.EnumerateFiles(DaemonLockPaths.Directory, "*.pid")
            .OrderBy(f => f)
            .ToList();

        if (pidFiles.Count == 0) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var entries = new List<(string Name, int Pid, bool Alive)>(pidFiles.Count);

        foreach (var pidFile in pidFiles) {
            var name = Path.GetFileNameWithoutExtension(pidFile);

            if (string.IsNullOrEmpty(name)) continue;

            var firstLine = (await File.ReadAllTextAsync(pidFile))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!int.TryParse(firstLine, out var pid)) continue;

            var alive = false;

            try {
                System.Diagnostics.Process.GetProcessById(pid);
                alive = true;
            } catch (ArgumentException) {
                // process gone; treated as stale below
            }

            entries.Add((name, pid, alive));
        }

        var live = entries.Where(e => e.Alive).ToList();

        switch (live.Count) {
            case 0:
                await Console.Out.WriteLineAsync(
                    entries.Count == 0
                        ? "not running"
                        : "not running (stale PID files; `kapacitor daemon doctor --clean` to remove)"
                );

                return;
            case 1:
                await Console.Out.WriteLineAsync($"running — {live[0].Name} (PID {live[0].Pid})");

                return;
            default: {
                var summary = string.Join(", ", live.Select(e => $"{e.Name} (PID {e.Pid})"));
                await Console.Out.WriteLineAsync($"running ({live.Count}) — {summary}");

                break;
            }
        }
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

                if (entries.Any(CodexHooksParser.EntryReferencesKapacitorCodexHook)) {
                    return true;
                }
            }

            return false;
        } catch {
            return false;
        }
    }
}
