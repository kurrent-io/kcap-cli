using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// On <c>SessionStart</c>, exports <c>KCAP_SESSION_ID</c> through the file Claude names in
/// <c>CLAUDE_ENV_FILE</c>, so the <c>kcap</c> commands the agent runs later find their own session.
/// </summary>
static partial class ClaudeSessionEnv {
    public const string EnvFileVar = "CLAUDE_ENV_FILE";

    /// <summary>Bash sources the file, so the id is checked rather than quoted, and the line ends in a
    /// bare LF on every platform: a CR would become part of the value.</summary>
    public static void Persist(string body, string? envFile, TextWriter stderr) {
        if (string.IsNullOrEmpty(envFile)) return;

        JsonObject? hook;
        try {
            hook = JsonNode.Parse(body) as JsonObject;
        } catch (JsonException) {
            return;
        }
        if (hook is null || Text(hook["hook_event_name"]) != "SessionStart") return;

        var sessionId = Text(hook["session_id"])?.Replace("-", "");
        if (sessionId is null || !SafeId().IsMatch(sessionId)) return;

        try {
            using var file = new FileStream(envFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            var unterminated = false;
            if (file.Length > 0) {
                file.Seek(-1, SeekOrigin.End);
                unterminated = file.ReadByte() != '\n';
            }
            file.Seek(0, SeekOrigin.End);
            file.Write(Encoding.UTF8.GetBytes($"{(unterminated ? "\n" : "")}export KCAP_SESSION_ID={sessionId}\n"));
        } catch (Exception e) {
            stderr.WriteLine($"kcap: KCAP_SESSION_ID not exported to {envFile}: {e.Message}");
        }
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SafeId();
}
