using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Hands the Claude <c>SessionEnd</c> event to a detached re-invocation of this hook so the hook
/// itself returns at once: Claude Code caps a plugin's SessionEnd hook at 1.5 s regardless of
/// the <c>hooks.json</c> timeout, and the session-end path cannot fit. The continuation runs that
/// path unchanged under its 15 s budget. Reasoning in <c>docs/CHANGES.md</c>.
/// </summary>
static class ClaudeSessionEndHandoff {
    public const string DetachedFlag = "--detached";

    public static bool IsDetached(string[] args) => args.Contains(DetachedFlag);

    public static bool ShouldHandOff(string[] args, string body) => !IsDetached(args) && IsSessionEnd(body);

    static bool IsSessionEnd(string body) {
        try {
            var name = JsonNode.Parse(body)?["hook_event_name"]?.GetValue<string>();

            return name is not null
                && name.Replace("-", "").Replace("_", "").Equals("sessionend", StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Starts the detached continuation and feeds it <paramref name="body"/>. False when that did
    /// not fully happen — the caller then runs the event inline.
    /// </summary>
    public static bool TrySpawn(string[] args, string body, ConfigRoot config, IProcessStarter starter) {
        Process? process = null;

        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            foreach (var arg in args) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(DetachedFlag);

            // The continuation resolves everything else for itself; the root is the one thing it must
            // not, or a hook and its own continuation would read different tokens.
            psi.Environment[ConfigRoot.ConfigDirEnvVar] = config.Directory;

            // Same pipe-leak hazard as the watcher spawn: the child must not hold Claude's hook
            // pipes open, or Claude waits on them past the hook's own exit.
            //
            // This is the one detached spawn that cannot use IProcessStarter.StartDetached: the
            // payload is handed over through the child's stdin, and a pipe can only reach a child
            // by being inherited. Windows therefore still inherits the rest of the agent's
            // handles here, so the leak survives on this path until the spawn passes an explicit
            // PROC_THREAD_ATTRIBUTE_HANDLE_LIST naming just that one pipe.
            ProcessHelpers.PreventInheritedHandles();

            process = starter.Start(psi);

            if (process is null) {
                Console.Error.WriteLine("[kcap] session-end hand-off: failed to start the detached continuation; running inline");

                return false;
            }

            process.StandardInput.Write(body);
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] session-end hand-off failed: {ex.Message}; running inline");

            // A child that started but never got the full payload must not outlive this failure:
            // the inline path is about to do the work, and two owners would double-post.
            try { process?.Kill(entireProcessTree: true); } catch { }

            return false;
        } finally {
            process?.Dispose();
        }
    }

    /// <summary>
    /// The continuation's own setup: output to the session log (its pipes are already closed) and
    /// out of the terminal's session so a closing window cannot SIGHUP it mid-drain.
    /// </summary>
    public static void EnterDetached(string body, ConfigRoot config) {
        TextWriter writer;

        try {
            var logDir = config.Path("logs");
            Directory.CreateDirectory(logDir);
            writer = new StreamWriter(Path.Combine(logDir, $"{LogName(body)}.log"), append: true) { AutoFlush = true };
        } catch {
            writer = TextWriter.Null;
        }

        Console.SetOut(writer);
        Console.SetError(writer);

        ProcessHelpers.DetachFromControllingTerminal();
    }

    // The watcher's log name for a well-formed id, so the two interleave in one file; anything
    // else (the id is payload data) stays inside the logs directory under a fixed name.
    internal static string LogName(string body) {
        try {
            var sessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>()?.Replace("-", "");

            if (sessionId is { Length: > 0 } && sessionId.All(char.IsAsciiLetterOrDigit)) return sessionId;
        } catch { }

        return "claude-session-end";
    }
}
