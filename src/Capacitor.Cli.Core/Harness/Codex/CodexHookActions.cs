using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Core.Harness.Codex;

/// A Codex hook names its calls with Claude's tool vocabulary (<c>Bash</c> with <c>tool_input.command</c>), so the
/// Claude normalizer reads them; the action is Codex's, which rules scoped to a vendor see. A file edit is the one
/// exception: <c>apply_patch</c> carries its patch in <c>tool_input.command</c>, whose headers name the files.
public static class CodexHookActions {
    const string Vendor = "codex";

    static readonly string[] FileHeaders = ["*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: "];

    public static CanonicalAction Normalize(string? toolName, JsonElement? toolInput, string? cwd) {
        if (toolName == "apply_patch" && Patch(toolInput) is { } patch) {
            var paths = PatchPaths(patch, cwd);
            // A patch naming a file that cannot be resolved is evaluated as Other, where no path rule can pass it.
            if (paths is { Count: > 0 })
                return new() { Kind = ActionKind.FileEdit, Vendor = Vendor, Cwd = cwd, Paths = paths, RawToolName = toolName };
        }

        return ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd) with { Vendor = Vendor };
    }

    static string? Patch(JsonElement? toolInput) =>
        toolInput is { ValueKind: JsonValueKind.Object } input && input.TryGetProperty("command", out var command)
     && command.ValueKind == JsonValueKind.String
            ? command.GetString()
            : null;

    /// Null when any header names a path that cannot be resolved.
    static IReadOnlyList<string>? PatchPaths(string patch, string? cwd) {
        var paths = new List<string>();

        foreach (var line in patch.Split('\n')) {
            var header = FileHeaders.FirstOrDefault(h => line.StartsWith(h, StringComparison.Ordinal));
            if (header is null) continue;
            if (LexicalPaths.TryResolve(cwd, line[header.Length..].TrimEnd('\r')) is not { } resolved) return null;
            if (!paths.Contains(resolved, StringComparer.Ordinal)) paths.Add(resolved);
        }

        return paths;
    }
}
