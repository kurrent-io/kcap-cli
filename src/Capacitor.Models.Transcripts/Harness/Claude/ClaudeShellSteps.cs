using System.Globalization;
using System.Text.Json;

namespace Capacitor.Models.Transcripts.Harness.Claude;

public static class ClaudeShellSteps {
    public static ShellSteps? Read(string line) {
        if (!line.Contains("tool_use", StringComparison.Ordinal)) return null;

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("message")?.Arr("content") is not { } content) return null;

            List<ShellSteps.Invocation> calls   = [];
            List<ShellSteps.Result>     results = [];

            foreach (var block in content.EnumerateArray()) {
                switch (block.Str("type")) {
                    case "tool_use" when block.Str("name") is "Bash" && block.Str("id") is { } id
                                      && block.Obj("input")?.Str("command") is { } command:
                        calls.Add(new(id, command));
                        break;

                    case "tool_result" when block.Str("tool_use_id") is { } of:
                        results.Add(new(of, Output(block), block.Bool("is_error") == true));
                        break;
                }
            }

            DateTimeOffset? at = DateTimeOffset.TryParse(root.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t
                : null;

            return new ShellSteps(root.Str("cwd"), at, calls, results);
        } catch (JsonException) {
            return null;
        }
    }

    static string Output(JsonElement result) =>
        result.Str("content") ?? (result.Arr("content") is { } parts
            ? string.Join('\n', parts.EnumerateArray().Select(part => part.Str("text")).OfType<string>())
            : "");
}
