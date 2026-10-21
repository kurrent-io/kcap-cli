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
        if (toolName == "apply_patch" && toolInput?.Str("command") is { } patch) {
            var paths = PatchPaths(patch, cwd);
            // A patch naming a file that cannot be resolved is evaluated as Other, where no path rule can pass it.
            if (paths is { Count: > 0 })
                return new() { Kind = ActionKind.FileEdit, Vendor = Vendor, Cwd = cwd, Paths = paths, RawToolName = toolName };
        }

        return ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd) with { Vendor = Vendor };
    }

    /// Null when any header names a path that cannot be resolved: an edit only partly placed must not be allowed
    /// by the part that was. A header naming no file at all is not a file the patch touches.
    static IReadOnlyList<string>? PatchPaths(string patch, string? cwd) {
        var paths = new List<string>();

        foreach (var raw in patch.Split('\n')) {
            var line   = raw.Trim();
            var header = FileHeaders.FirstOrDefault(h => line.StartsWith(h.TrimEnd(), StringComparison.Ordinal));
            if (header is null) continue;
            var path = line[header.TrimEnd().Length..].Trim();
            if (path.Length == 0) continue;
            if (LexicalPaths.TryResolve(cwd, path) is not { } resolved) return null;
            if (!paths.Contains(resolved, StringComparer.Ordinal)) paths.Add(resolved);
        }

        return paths;
    }
}
