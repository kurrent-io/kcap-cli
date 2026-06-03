using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

static class WatcherManager {
    internal static string GetWatcherDir() {
        var overrideDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");

        return overrideDir ?? PathHelpers.ConfigPath("watchers");
    }

    static string GetPidFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.pid");

    internal static string BuildSpawnArgs(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride,
            string? cwd,
            bool    skipTitle,
            int?    parentPid,
            string  vendor
        ) {
        var sessionId = sessionIdOverride ?? key;

        var arguments = agentId is not null
            ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
            : $"watch {key} \"{transcriptPath}\"";

        if (cwd is not null) {
            arguments += $" --cwd \"{cwd}\"";
        }

        if (skipTitle) {
            arguments += " --skip-title";
        }

        if (parentPid is { } ppid and > 1) {
            arguments += $" --parent-pid {ppid}";
        }

        if (vendor != "claude") {
            arguments += $" --vendor \"{vendor}\"";
        }

        return arguments;
    }

    public static async Task SpawnWatcher(
            string  baseUrl,
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride = null,
            string? cwd               = null,
            bool    skipTitle         = false,
            string  vendor            = "claude"
        ) {
        try {
            var watcherDir = GetWatcherDir();
            Directory.CreateDirectory(watcherDir);

            var kcapPath = Environment.ProcessPath ?? "kcap";
            // Use the coding-agent's PID (process group leader on Unix) rather than
            // getppid(): coding agents invoke hooks through a transient executor that
            // dies the moment the hook returns, so by the time the watcher checks
            // IsProcessAlive it sees a dead PID and never starts the monitor task —
            // leaving Codex sessions stuck "active" because session-end is never POSTed.
            var parentPid     = ProcessHelpers.GetCodingAgentPid();
            var arguments     = BuildSpawnArgs(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, parentPid, vendor);

            var psi = new ProcessStartInfo(kcapPath, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment            = { ["KCAP_URL"] = baseUrl }
            };

            var process = Process.Start(psi);

            if (process is null) {
                await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}");

                return;
            }

            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            await File.WriteAllTextAsync(GetPidFilePath(key), process.Id.ToString());
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills the watcher process for the given key. Returns true if the watcher was running and was killed,
    /// false if it was already dead or no PID file existed.
    /// </summary>
    public static async Task<bool> KillWatcher(string key) {
        var pidFile = GetPidFilePath(key);

        if (!File.Exists(pidFile)) {
            return false;
        }

        try {
            var pidText = (await File.ReadAllTextAsync(pidFile)).Trim();

            if (!int.TryParse(pidText, out var pid)) {
                File.Delete(pidFile);

                return false;
            }

            try {
                var process = Process.GetProcessById(pid);

                // Send SIGTERM
                process.Kill(entireProcessTree: false);

                // Wait up to 5 seconds for graceful exit
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                try {
                    await process.WaitForExitAsync(cts.Token);
                    await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) exited gracefully");
                } catch (OperationCanceledException) {
                    // Force kill if it didn't exit in time
                    process.Kill(entireProcessTree: true);
                    await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) force-killed after timeout");
                }

                return true;
            } catch (ArgumentException) {
                // Process already exited
                await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) already exited");

                return false;
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Error killing watcher {key}: {ex.Message}");

            return false;
        } finally {
            try { File.Delete(pidFile); } catch {
                /* ignore */
            }
        }
    }

    static bool IsWatcherAlive(string key) {
        var pidFile = GetPidFilePath(key);

        if (!File.Exists(pidFile)) {
            return false;
        }

        try {
            var pidText = File.ReadAllText(pidFile).Trim();

            if (!int.TryParse(pidText, out var pid)) {
                return false;
            }

            try {
                var process = Process.GetProcessById(pid);

                return !process.HasExited;
            } catch (ArgumentException) {
                return false;
            }
        } catch {
            return false;
        }
    }

    public static async Task EnsureWatcherRunning(
            string  baseUrl,
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride = null,
            string? cwd               = null,
            bool    skipTitle         = false,
            string  vendor            = "claude"
        ) {
        if (IsWatcherAlive(key)) {
            return;
        }

        await SpawnWatcher(baseUrl, key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor);
    }

    public static void SpawnWhatsDoneGenerator(string baseUrl, string sessionId, string vendor = "claude") {
        try {
            var kcapPath = Environment.ProcessPath ?? "kcap";

            var psi = new ProcessStartInfo(kcapPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"] = baseUrl
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            // The child process picks the headless CLI runner from this flag —
            // matches the `generate-whats-done [--codex] <id>` surface in Program.cs.
            if (vendor == "codex") {
                psi.ArgumentList.Add("--codex");
            }

            var process = Process.Start(psi);

            if (process is null) {
                Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}");

                return;
            }

            // Close redirected streams from parent side so the child doesn't hold pipe FDs open
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            Console.Error.WriteLine($"Spawned what's-done generator for {sessionId} (PID {process.Id})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}: {ex.Message}");
        }
    }

    public static async Task InlineDrainAsync(
            string  baseUrl,
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string  vendor = "claude"
        ) {
        try {
            using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            // Get server's last recorded position
            int startLine;

            try {
                var query = agentId is not null ? $"?agentId={agentId}" : "";
                var resp  = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line{query}");

                if (resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent) {
                    var json = await resp.Content.ReadAsStringAsync();
                    var doc  = JsonDocument.Parse(json);

                    startLine = (int?)doc.RootElement.Num("last_line_number") + 1
                     ?? WatchCommand.CountFileLines(transcriptPath);
                } else {
                    startLine = WatchCommand.CountFileLines(transcriptPath);
                }
            } catch {
                startLine = WatchCommand.CountFileLines(transcriptPath);
            }

            if (!File.Exists(transcriptPath)) {
                return;
            }

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            var lineIndex = 0;

            while (await reader.ReadLineAsync() is { } line) {
                if (lineIndex < startLine) {
                    lineIndex++;

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            if (newLines.Count == 0) {
                await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: no new lines to send");

                return;
            }

            var batch = new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = [..newLines],
                LineNumbers = [..newLineNumbers],
                Vendor      = vendor == "claude" ? null : vendor
            };

            var       batchJson = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);
            using var content   = new StringContent(batchJson, Encoding.UTF8, "application/json");

            try {
                var resp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/transcript", content);

                if (resp.IsSuccessStatusCode) {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: sent {newLines.Count} line(s)");
                } else {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server returned HTTP {(int)resp.StatusCode}");
                    PrintRecoveryHint(sessionId);
                }
            } catch (HttpRequestException ex) {
                await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server unreachable after retries — {ex.Message}");
                PrintRecoveryHint(sessionId);
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Inline drain for {sessionId} failed: {ex.Message}");
            PrintRecoveryHint(sessionId);
        }
    }

    static void PrintRecoveryHint(string sessionId) =>
        Console.Error.WriteLine($"Transcript not uploaded. To import later, run: kcap import --session {sessionId}");
}
