using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>Probes a Claude harness for its slash commands WITHOUT starting a session: runs
/// <c>claude -p --input-format stream-json</c>, sends one <c>initialize</c> control request, reads the
/// commands off the control response, and lets the process exit at stdin EOF. No user message is sent,
/// so no model API call is made. Best-effort — every failure returns an empty list and never throws,
/// so a broken probe degrades the picker to "no commands" rather than touching the launch.
///
/// <para><c>KCAP_SKIP=1</c> stops the probe's SessionStart hooks from recording a phantom session (the
/// kcap hook no-ops on it) — <c>--bare</c> would skip hooks too but also drops the plugins whose
/// commands the picker exists to show. An empty strict MCP config keeps the probe from spawning the
/// worktree's MCP servers, which contribute none of the built-in / plugin / project commands.</para></summary>
static class ClaudeCommandProbe {
    const string InitializeRequest =
        """{"type":"control_request","request_id":"kcap-command-probe","request":{"subtype":"initialize"}}""";

    public static async Task<IReadOnlyList<HostedAgentCommand>> ProbeAsync(
            HarnessRegistry   harnesses,
            string            workingDir,
            TimeProvider      time,
            TimeSpan          timeout,
            Action<string>    log,
            CancellationToken ct) {
        // Resolve rather than spawn "claude" verbatim, for the same reason ClaudeCliRunner does: on
        // Windows the npm shim is claude.cmd, which a bare FileName would never find.
        var exePath = harnesses.ResolveExecutable(HarnessId.Claude);
        if (exePath is null) return [];

        var psi = new ProcessStartInfo {
            FileName               = exePath,
            WorkingDirectory       = workingDir,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment            = { ["KCAP_SKIP"] = "1" },
        };
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        foreach (var arg in new[] {
                     "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                     "--verbose", "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}""",
                 })
            psi.ArgumentList.Add(arg);

        using var process = CliProcess.TryStart(psi, log);
        if (process is null) return [];

        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            // Explicit "\n", not WriteLineAsync: on Windows the latter writes "\r\n" and the trailing
            // \r rides into the JSON line the stream-json parser reads.
            await process.StandardInput.WriteAsync((InitializeRequest + "\n").AsMemory(), linkedCts.Token);
            await process.StandardInput.FlushAsync(linkedCts.Token);
            process.StandardInput.Close();

            // Drain stderr concurrently: verbose Claude diagnostics and hook output can fill the stderr
            // pipe, and a child blocked writing to a full stderr never closes stdout or exits — the
            // probe would then hit its timeout and return nothing.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            await stderrTask;

            return ParseCommands(stdout);
        } catch (Exception ex) {
            log($"claude command probe failed: {ex.Message}");

            // ProcessTree.Kill, never Process.Kill(bool): this child shares the daemon's process group,
            // and the daemon's tree-kill (SIGKILL only, children before parents, each pid identity-checked)
            // is the one sanctioned way to stop it.
            try { ProcessTree.Kill(process); } catch {
                /* best-effort */
            }

            return [];
        }
    }

    internal static IReadOnlyList<HostedAgentCommand> ParseCommands(string stdout) {
        foreach (var line in stdout.Split('\n')) {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            if (TryParseControlResponse(trimmed) is { } commands) return commands;
        }

        return [];
    }

    // The initialize response is one line: {type:control_response, response:{subtype:success,
    // response:{commands:[{name,description,argumentHint}], ...}}}.
    static IReadOnlyList<HostedAgentCommand>? TryParseControlResponse(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.Str("type") != "control_response") return null;
            if (root.Obj("response") is not { } response) return null;
            if (response.Obj("response") is not { } inner) return null;
            if (inner.Arr("commands") is not { } commands) return null;

            var list = new List<HostedAgentCommand>();

            foreach (var entry in commands.EnumerateArray()) {
                if (!entry.IsObject) continue;

                var name = entry.Str("name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add(new HostedAgentCommand(
                    name,
                    NullIfBlank(entry.Str("description")),
                    NullIfBlank(entry.Str("argumentHint"))));
            }

            return list;
        } catch (JsonException) {
            return null;
        }
    }

    static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
