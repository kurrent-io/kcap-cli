using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.App.ViewModels;

/// The one-line detail a tool row shows beside its name, read from the call's input object. A
/// path under the session's root reads relative to it, the way the web UI shows it. The root is
/// the checkout the agent runs in. An older daemon sends no worktree, only its RepoPath — the
/// repository for a primary, the borrowed checkout for a reviewer — so a daemon worktree beneath
/// a repository root is stripped as well.
public static class ToolDetail {
    const int MaxLength = 80;
    /// A question is read rather than scanned — the row wraps it instead of eliding — so the only
    /// cap on it is the one that keeps an unbounded prompt out of a notification body.
    const int MaxQuestionLength = 400;
    const string WorktreesSegment = "/.capacitor/worktrees/";

    static readonly string[] Keys = [
        "description", "command", "cmd", "file_path", "path", "notebook_path", "pattern", "query", "url", "skill", "prompt", "input",
    ];
    static readonly string[] PathKeys = ["file_path", "path", "notebook_path"];
    static readonly string[] PatchTextKeys = ["input", "command"];

    public static string From(string? inputJson, string? root = null, ToolCategory category = ToolCategory.Other) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.IsObject) return "";
            if (doc.RootElement.Arr("questions") is { } questions) {
                foreach (var item in questions.EnumerateArray()) {
                    if (item.Str("question") is { } text && text.Trim().Length > 0)
                        return TextElision.End(FirstLine(text), MaxQuestionLength);
                }
            }
            if (category == ToolCategory.Question && doc.RootElement.Str("prompt") is { } prompt && prompt.Trim().Length > 0)
                return TextElision.End(FirstLine(prompt), MaxQuestionLength);
            if (PatchTargets(doc.RootElement, category) is { } targets)
                return PatchDetail(targets, root);
            foreach (var key in Keys) {
                if (category == ToolCategory.Question && key == "prompt") continue;
                if (doc.RootElement.Str(key) is { } s && s.Trim().Length > 0)
                    return TextElision.Middle(FirstLine(PathKeys.Contains(key) ? Relative(s.Trim(), root) : s), MaxLength);
            }
        } catch (JsonException) { }
        return "";
    }

    /// The files a patch call names; null when the input is not a patch, empty when it is one whose
    /// targets cannot be read. A Codex patch rides as <c>input</c> in its rollout and as
    /// <c>command</c> in its hook; a daemon's live edit lists <c>changes[].path</c>.
    public static IReadOnlyList<string>? PatchTargets(string? inputJson, ToolCategory category) {
        if (string.IsNullOrEmpty(inputJson)) return null;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.IsObject ? PatchTargets(doc.RootElement, category) : null;
        } catch (JsonException) {
            return null;
        }
    }

    static IReadOnlyList<string>? PatchTargets(JsonElement input, ToolCategory category) {
        foreach (var key in PatchTextKeys)
            if (CodexPatch.Targets(input.Str(key)) is { } targets) return targets;
        if (category != ToolCategory.Edit || input.Arr("changes") is not { } changes) return null;
        var paths = new List<string>();
        foreach (var change in changes.EnumerateArray()) {
            var path = change.Str("path")?.Trim();
            if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path);
        }
        return paths;
    }

    /// One file reads as its path; several as a list while it fits, then the first and a count.
    static string PatchDetail(IReadOnlyList<string> targets, string? root) {
        if (targets.Count == 0) return "";
        var paths = targets.Select(t => Relative(t, root)).ToList();
        if (paths.Count == 1) return TextElision.Middle(paths[0], MaxLength);
        var list = string.Join(", ", paths);
        if (list.Length <= MaxLength) return list;
        var more = $" +{paths.Count - 1} more";
        return TextElision.Middle(paths[0], MaxLength - more.Length) + more;
    }

    /// The one argument a catalogued kcap row shows, first line only, elided like any detail.
    public static string ForKey(string? inputJson, string? key) {
        var raw = RawForKey(inputJson, key);
        return raw.Length > 0 ? TextElision.End(FirstLine(raw), MaxLength) : "";
    }

    public static string RawForKey(string? inputJson, string? key) {
        if (string.IsNullOrEmpty(inputJson) || string.IsNullOrEmpty(key)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.IsObject) return "";
            return doc.RootElement.Str(key) is { } s && s.Trim().Length > 0 ? FirstLine(s) : "";
        } catch (JsonException) {
            return "";
        }
    }

    public static string? ImageFile(string? inputJson) {
        foreach (var key in PathKeys) {
            var path = RawForKey(inputJson, key);
            if (path.Length == 0) continue;
            if (IsImageExtension(path)) return path;
        }
        return null;
    }

    internal static string? ResolvePreviewPath(string? path, string? root) {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try {
            if (Path.IsPathFullyQualified(path)) return path;
            return string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root)
                ? null : Path.GetFullPath(path, root);
        } catch (ArgumentException) {
            return null;
        }
    }

    internal static bool IsImageExtension(string path) {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// For a tool nothing describes: the first string argument, in declaration order.
    public static string FirstString(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (!doc.RootElement.IsObject) return "";
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.IsString && property.Value.GetString() is { } s && s.Trim().Length > 0)
                    return TextElision.End(FirstLine(s), MaxLength);
        } catch (JsonException) { }
        return "";
    }

    static string Relative(string path, string? root) {
        if (string.IsNullOrEmpty(root)) return path;
        var prefix = root.TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return path;
        var rest = path[prefix.Length..];
        if (rest.StartsWith(WorktreesSegment[1..], StringComparison.Ordinal)) {
            var afterWorktree = rest.IndexOf('/', WorktreesSegment.Length - 1);
            if (afterWorktree >= 0) rest = rest[(afterWorktree + 1)..];
        }
        return rest.Length == 0 ? path : rest;
    }

    static string FirstLine(string text) {
        var line = text.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        return newline < 0 ? line : line[..newline].TrimEnd();
    }
}
