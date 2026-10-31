using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class ErrorsCommand(ISessionsApi sessionsApi) {
    public async Task<int> HandleErrors(string sessionId, bool chain) {
        ErrorsResult result;

        try {
            result = await sessionsApi.GetErrorsAsync(sessionId, chain);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is ErrorsResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var errors = ((ErrorsResult.Found)result).Errors;

        if (errors.Count == 0) {
            await Console.Out.WriteLineAsync("No errors found.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Found {errors.Count} error(s):\n");

        foreach (var error in errors) await Console.Out.WriteAsync(Render(error));

        return 0;
    }

    const int InputWidth = 200;

    /// <summary>One error: where it happened, what the call was given, and every line of the message at the
    /// same indent, so a multi-line error stays inside its entry.</summary>
    internal static string Render(ErrorEntry error) {
        var sb       = new StringBuilder();
        var label    = error.SessionSlug ?? error.SessionId;
        var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";

        sb.AppendLine($"  [{label}]{agentTag} #{error.EventNumber} {error.ToolName ?? "unknown"}");

        if (InputSummary(error.ToolInput) is { } input) sb.AppendLine($"    input: {input}");

        foreach (var line in error.Error.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')) sb.AppendLine($"    {line}");

        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>What the failed call acted on: its path, command, pattern or address when it names one,
    /// else its whole input. One line, cut to <see cref="InputWidth"/> after credentials are redacted, so a
    /// cut cannot leave half a secret the patterns no longer match.</summary>
    internal static string? InputSummary(JsonElement? input) {
        if (input is not { } el || el.IsNull) return null;

        string? named = null;

        foreach (var key in (string[])["file_path", "filePath", "path", "notebook_path", "command", "cmd", "pattern", "url", "query"]) {
            if (el.Prop(key) is not { } value) continue;

            named = value.IsString ? value.GetString()
                  : value.IsArray ? string.Join(' ', value.EnumerateArray().Select(v => v.IsString ? v.GetString() : v.GetRawText()))
                  : null;

            if (named is { Length: > 0 }) break;
        }

        var text = named is { Length: > 0 } ? Redacted(named)
                 : el.IsString ? Redacted(el.GetString() ?? "")
                 : SecretRedactor.RedactLine(el.GetRawText());
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return text.Length == 0 ? null : HttpClientExtensions.StripControlCharacters(text.Length > InputWidth ? text[..(InputWidth - 3)] + "..." : text);

        static string Redacted(string value) => SecretRedactor.RedactValue(value, keyIsSecret: false) ?? value;
    }
}
