using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// <c>kcap hook --claude --title-prompt</c>: on a session's first <c>UserPromptSubmit</c>, asks the
/// agent to name the session with <c>kcap set-title</c>. Local only, and dispatched before repository
/// resolution, because Claude gives this hook two seconds.
/// </summary>
static partial class ClaudeTitlePrompt {
    public const string Flag = "--title-prompt";

    const string Instruction =
        "<system-instructions>\n"
      + "After reading the user's message and understanding the task, silently run this command via Bash (do not mention it to the user):\n"
      + "kcap set-title \"<your concise 5-10 word title summarizing the task>\"\n"
      + "The title should describe WHAT the user wants done, not HOW. Use imperative form (e.g. \"Fix authentication timeout in login flow\"). No period at the end.\n"
      + "</system-instructions>";

    public static int Handle(string body, ConfigRoot config, WatcherPaths watchers, TextWriter stdout, TextWriter stderr) {
        string? sessionId;
        try {
            sessionId = JsonNode.Parse(body)?["session_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        } catch (JsonException) {
            return 0;
        }
        if (sessionId is null || !SafeId().IsMatch(sessionId)) return 0;

        try {
            if (DisabledSessions.IsDisabled(sessionId.Replace("-", ""), config)) return 0;

            // The marker goes down before the output: a marker that cannot be written must not turn
            // into the same instruction on every prompt.
            var marker = Path.Combine(watchers.Directory, $"{sessionId}.title-requested");
            if (File.Exists(marker)) return 0;
            Directory.CreateDirectory(watchers.Directory);
            File.WriteAllText(marker, "");

            stdout.WriteLine(new JsonObject {
                ["hookSpecificOutput"] = new JsonObject {
                    ["hookEventName"]     = "UserPromptSubmit",
                    ["additionalContext"] = Instruction
                }
            }.ToJsonString());
        } catch (Exception e) {
            stderr.WriteLine($"kcap: title prompt for session {sessionId} skipped: {e.Message}");
        }

        return 0;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex SafeId();
}
