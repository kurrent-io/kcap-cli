using System.Diagnostics;
using System.Text;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Headless invocation of the <c>codex exec</c> CLI for title generation and
/// what's-done summaries on Codex-vendor sessions.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ClaudeCliRunner"/>'s contract — same return record, same
/// timeout / cancellation semantics — but the wire is much simpler: <c>codex
/// exec</c> writes a single text response to <c>--output-last-message &lt;file&gt;</c>
/// which we read back as <see cref="ClaudeCliResult.Result"/>. Token usage and
/// cost are not currently parsed (would require <c>--json</c> event-stream
/// processing); they default to zero.
/// </remarks>
static class CodexCliRunner {
    public static async Task<ClaudeCliResult?> RunAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string            model         = "gpt-5.3-codex",
            string            reasoning     = "low",
            CancellationToken ct            = default
        ) {
        // Fresh empty working dir per invocation: --skip-git-repo-check is
        // passed below so codex won't refuse to run, but a stable per-session
        // dir keeps any incidental writes (cache, temp probes) isolated and
        // sweepable.
        var workingDir        = Path.Combine(Path.GetTempPath(), $"kapacitor-codex-{Guid.NewGuid():N}");
        var lastMessageFile   = Path.Combine(workingDir, "last-message.txt");
        var createdWorkingDir = false;

        try {
            Directory.CreateDirectory(workingDir);
            createdWorkingDir = true;
        } catch (Exception ex) {
            log($"Failed to create isolated working directory: {ex.Message}");
            workingDir      = Path.GetTempPath();
            lastMessageFile = Path.Combine(workingDir, $"kapacitor-codex-last-{Guid.NewGuid():N}.txt");
        }

        try {
            return await RunCoreAsync(prompt, timeout, log, workingDir, lastMessageFile, model, reasoning, ct);
        } finally {
            if (createdWorkingDir) {
                try { Directory.Delete(workingDir, recursive: true); } catch {
                    /* best effort */
                }
            } else {
                try { File.Delete(lastMessageFile); } catch {
                    /* best effort */
                }
            }
        }
    }

    static async Task<ClaudeCliResult?> RunCoreAsync(
            string            prompt,
            TimeSpan          timeout,
            Action<string>    log,
            string            workingDir,
            string            lastMessageFile,
            string            model,
            string            reasoning,
            CancellationToken ct
        ) {
        var psi = new ProcessStartInfo {
            FileName               = "codex",
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                // Symmetric with the Claude path — block any nested kapacitor hook
                // forwarding so a headless codex invocation can't re-trigger import.
                ["KAPACITOR_SKIP"] = "1"
            }
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--ephemeral");
        psi.ArgumentList.Add("--ignore-user-config");
        psi.ArgumentList.Add("--ignore-rules");
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("read-only");
        psi.ArgumentList.Add("--color");
        psi.ArgumentList.Add("never");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"model_reasoning_effort=\"{reasoning}\"");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--output-last-message");
        psi.ArgumentList.Add(lastMessageFile);
        // Read prompt from stdin via the literal "-" placeholder so titles longer
        // than the OS argv limit (and embedded special characters like "$" or
        // backticks) don't need escaping.
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi);

        if (process is null) {
            log("Failed to start codex process");

            return null;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), linkedCts.Token);
            await process.StandardInput.FlushAsync(linkedCts.Token);
            process.StandardInput.Close();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            throw;
        } catch (Exception ex) {
            log($"Failed to stream prompt to codex stdin: {ex.Message}");
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            return null;
        }

        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token);
            await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0) {
                var stderrPreview = stderr.Length > 200 ? stderr[..200] : stderr;
                log($"Codex exited with code {process.ExitCode}, stderr: {stderrPreview}");

                return null;
            }

            if (!File.Exists(lastMessageFile)) {
                log("Codex exited 0 but last-message file is missing");

                return null;
            }

            var resultText = (await File.ReadAllTextAsync(lastMessageFile, Encoding.UTF8, linkedCts.Token)).Trim();

            if (string.IsNullOrWhiteSpace(resultText)) {
                log("Codex produced an empty last message");

                return null;
            }

            // Token usage / cost are not parsed: would require --json event-stream
            // processing. Title and what's-done payloads tolerate zeros (they're
            // surfaced for diagnostics, not billing).
            return new ClaudeCliResult(
                Result: resultText,
                Model: model,
                InputTokens: 0,
                OutputTokens: 0,
                CacheReadTokens: 0,
                CacheWriteTokens: 0,
                CostUsd: null,
                NumTurns: 1
            );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            throw;
        } catch (OperationCanceledException) {
            log($"Codex process timed out ({timeout.TotalSeconds:0}s), killing");

            try { process.Kill(entireProcessTree: true); } catch {
                /* ignore */
            }

            return null;
        }
    }
}
