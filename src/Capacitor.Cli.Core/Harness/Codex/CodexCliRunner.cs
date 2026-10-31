using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Headless invocation of <c>codex exec</c>, returning the same records as <see cref="ClaudeCliRunner"/>.
/// </summary>
/// <remarks>
/// Codex has no turn cap and no system-prompt replacement: the timeout is the only bound on the run,
/// and a system prompt travels as a delimited preamble of the prompt text.
/// </remarks>
static class CodexCliRunner {
    /// <summary>Adapts <see cref="RunDetailedAsync"/> to the null-on-any-failure contract.</summary>
    public static async Task<ClaudeCliResult?> RunAsync(
            string            prompt,
            TimeSpan          timeout,
            TimeProvider      time,
            Action<string>    log,
            Profile?          profile,
            HarnessRegistry   harnesses,
            string?           model        = null,
            string            reasoning    = "low",
            string?           jsonSchema   = null,
            string?           systemPrompt = null,
            CancellationToken ct           = default
        ) =>
        (await RunDetailedAsync(prompt, timeout, time, log, profile, harnesses, model, reasoning, jsonSchema, systemPrompt, ct)).Result;

    /// <summary>
    /// Runs the prompt once in a read-only sandbox inside a fresh empty directory, which is removed
    /// afterwards. With <paramref name="jsonSchema"/>, the result is the matched object, re-serialised.
    /// External cancellation kills the process and propagates; the internal timeout kills it and
    /// returns <see cref="ClaudeCliFailure.Timeout"/>.
    /// </summary>
    public static async Task<ClaudeCliOutcome> RunDetailedAsync(
            string            prompt,
            TimeSpan          timeout,
            TimeProvider      time,
            Action<string>    log,
            Profile?          profile,
            HarnessRegistry   harnesses,
            string?           model        = null,
            string            reasoning    = "low",
            string?           jsonSchema   = null,
            string?           systemPrompt = null,
            CancellationToken ct           = default
        ) {
        var workingDir = Path.Combine(Path.GetTempPath(), $"kcap-codex-{Guid.NewGuid():N}");

        try {
            Directory.CreateDirectory(workingDir);
        } catch (Exception ex) {
            // The empty directory is part of the lockdown: the sandbox can read whatever the cwd holds.
            log($"Failed to create isolated working directory: {ex.Message}");

            return new(null, ClaudeCliFailure.ProcessFailure);
        }

        try {
            return await RunCoreAsync(prompt, timeout, time, log, profile, harnesses, workingDir, model, reasoning, jsonSchema, systemPrompt, ct);
        } finally {
            try { Directory.Delete(workingDir, recursive: true); } catch {
                /* best effort */
            }
        }
    }

    static async Task<ClaudeCliOutcome> RunCoreAsync(
            string            prompt,
            TimeSpan          timeout,
            TimeProvider      time,
            Action<string>    log,
            Profile?          profile,
            HarnessRegistry   harnesses,
            string            workingDir,
            string?           model,
            string            reasoning,
            string?           jsonSchema,
            string?           systemPrompt,
            CancellationToken ct
        ) {
        // Resolve rather than pass "codex" verbatim: CreateProcess appends only .exe, so the
        // npm-installed codex.cmd shim on Windows would never be found.
        var exePath = harnesses.ResolveLaunchable(HarnessId.Codex);

        if (exePath is null) {
            log("codex not found on PATH or in the desktop app");

            return new(null, ClaudeCliFailure.ProcessFailure);
        }

        var lastMessageFile = Path.Combine(workingDir, "last-message.txt");
        string? schemaFile  = null;

        if (!string.IsNullOrWhiteSpace(jsonSchema)) {
            schemaFile = Path.Combine(workingDir, "output-schema.json");
            await File.WriteAllTextAsync(schemaFile, jsonSchema, ct);
        }

        var psi = new ProcessStartInfo {
            FileName               = exePath,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                // A headless codex must not forward its own hooks back into kcap.
                [HostedAgent.SkipVar] = "1"
            }
        };
        // A globally-set OPENAI_API_KEY overrides ChatGPT subscription auth in `codex exec`.
        if (!ProviderApiKeyPolicy.ShouldKeepProviderKey(profile)) {
            psi.Environment.Remove("OPENAI_API_KEY");
        }

        foreach (var arg in BuildCodexArgs(lastMessageFile, schemaFile, model, reasoning)) {
            psi.ArgumentList.Add(arg);
        }

        using var process = CliProcess.TryStart(psi, log);

        if (process is null) {
            log("Failed to start codex process");

            return new(null, ClaudeCliFailure.ProcessFailure);
        }

        // Read without a token so the events seen before a kill survive it.
        var events     = new CodexExecEvents();
        var stdoutTask = ReadEventsAsync(process.StandardOutput, events);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.StandardInput.WriteAsync(BuildPrompt(prompt, systemPrompt).AsMemory(), linkedCts.Token);
            await process.StandardInput.FlushAsync(linkedCts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(linkedCts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            KillQuietly(process);

            throw;
        } catch (OperationCanceledException) {
            log($"Codex process timed out ({timeout.TotalSeconds:0}s), killing");
            KillQuietly(process);

            return new(null, ClaudeCliFailure.Timeout, await UsageAfterKillAsync(process, stdoutTask, events, time));
        } catch (Exception ex) {
            log($"Failed to stream prompt to codex stdin: {ex.Message}");
            KillQuietly(process);

            return new(null, ClaudeCliFailure.ProcessFailure, await UsageAfterKillAsync(process, stdoutTask, events, time));
        }

        try {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            // A descendant that outlived codex holds the pipe; codex itself has written all it will.
            log("Codex exited but its output stayed open to the deadline; using what was read");
            CloseOutput(process);
        }

        var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";

        if (process.ExitCode != 0 || events.TurnFailed) {
            // Codex prints a multi-line session header first, so the useful part of stderr is its tail.
            var stderrTail = stderr.Length > 800 ? "…" + stderr[^800..] : stderr;
            log($"Codex exited with code {process.ExitCode}: {SanitizeForLog(events.FailureMessage ?? "")}, stderr: {SanitizeForLog(stderrTail)}");

            return new(null, CodexExecEvents.ClassifyFailure(events.FailureMessage), events.Usage);
        }

        var resultText = File.Exists(lastMessageFile)
            ? (await File.ReadAllTextAsync(lastMessageFile, Encoding.UTF8, ct)).Trim()
            : events.LastAgentMessage?.Trim();

        if (string.IsNullOrWhiteSpace(resultText)) {
            log("Codex produced an empty last message");

            return new(null, ClaudeCliFailure.OutputUnparseable, events.Usage);
        }

        if (schemaFile is not null) {
            if (StructuredResult(resultText) is not { } structured) {
                log("Codex reply rejected: --output-schema was required and not satisfied");

                return new(null, ClaudeCliFailure.OutputUnparseable, events.Usage);
            }

            resultText = structured;
        }

        var usage = events.Usage;

        return new(
            new ClaudeCliResult(
                Result: resultText,
                Model: string.IsNullOrWhiteSpace(model) ? null : model,
                InputTokens: usage?.InputTokens           ?? 0,
                OutputTokens: usage?.OutputTokens         ?? 0,
                CacheReadTokens: usage?.CacheReadTokens   ?? 0,
                CacheWriteTokens: usage?.CacheWriteTokens ?? 0,
                CostUsd: null,
                NumTurns: Math.Max(1, events.CompletedTurns)
            ),
            null,
            usage
        );
    }

    /// <summary>
    /// Builds the ordered <c>codex</c> argument list. The prompt goes on stdin (<c>-</c>), never on
    /// the command line, so its length and characters need no escaping.
    /// </summary>
    internal static List<string> BuildCodexArgs(string lastMessageFile, string? outputSchemaFile, string? model, string reasoning) {
        List<string> args = [
            "exec",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--skip-git-repo-check",
            "--sandbox", "read-only",
            "-c", "approval_policy=\"never\"",
            "-c", "sandbox_workspace_write.network_access=false",
            // Hosted web search runs on the provider's side, outside the local sandbox.
            "-c", "web_search=\"disabled\"",
            "--color", "never",
            "--json",
            "-c", $"model_reasoning_effort=\"{reasoning}\""
        ];

        // No default model: a ChatGPT-account login rejects some model names, and codex's own pick works for both.
        if (!string.IsNullOrWhiteSpace(model)) {
            args.Add("-m");
            args.Add(model);
        }

        if (outputSchemaFile is not null) {
            args.Add("--output-schema");
            args.Add(outputSchemaFile);
        }

        args.Add("--output-last-message");
        args.Add(lastMessageFile);
        args.Add("-");

        return args;
    }

    internal static string BuildPrompt(string prompt, string? systemPrompt) =>
        string.IsNullOrWhiteSpace(systemPrompt)
            ? prompt
            : $"<system_prompt>\n{systemPrompt.Trim()}\n</system_prompt>\n\n{prompt}";

    /// <summary>The reply re-serialised when it is a JSON object or array, else null.</summary>
    internal static string? StructuredResult(string reply) {
        try {
            using var doc = JsonDocument.Parse(reply);

            return doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? doc.RootElement.GetRawText() : null;
        } catch (JsonException) {
            return null;
        }
    }

    static async Task ReadEventsAsync(StreamReader stdout, CodexExecEvents events) {
        try {
            while (await stdout.ReadLineAsync() is { } line) {
                events.Accept(line);
            }
        } catch (Exception e) when (e is ObjectDisposedException or IOException) { }
    }

    /// <summary>A grandchild that inherited stdout can hold the pipe open past the kill, so the read is bounded.</summary>
    static async Task<ClaudeCliUsage?> UsageAfterKillAsync(Process process, Task stdoutTask, CodexExecEvents events, TimeProvider time) {
        try {
            await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), time);
        } catch {
            CloseOutput(process);
        }

        return events.Usage;
    }

    /// <summary>Ends reads a descendant would otherwise keep pending: the process does not dispose its own readers.</summary>
    static void CloseOutput(Process process) {
        try { process.StandardOutput.Dispose(); } catch { /* best effort */ }
        try { process.StandardError.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>The process can exit between the kill's liveness check and its signal.</summary>
    static void KillQuietly(Process process) {
        try { ProcessTree.Kill(process); } catch {
            /* already gone */
        }
    }

    /// <summary>Folds codex's multi-line output onto one log line.</summary>
    static string SanitizeForLog(string value) {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value) {
            if (ch is '\r' or '\n') {
                sb.Append("\\n");
                continue;
            }

            if (char.IsControl(ch)) continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
