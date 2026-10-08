using Capacitor.Cli.Core.Toml;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>
/// Detect + install/remove kcap's lifecycle hooks in Mistral Vibe's <c>~/.vibe/hooks.toml</c>. The
/// read-modify-write runs through <see cref="TomlConfigFile"/> (atomic, locked, symlink-rejecting);
/// the merge preserves any hook entries the user authored and only touches the kcap-owned ones.
/// </summary>
public static class MistralVibeHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>True when kcap's Vibe hooks were previously installed — a marker file, or an existing
    /// <c>kcap hook --mistral-vibe</c> entry in <c>hooks.toml</c> (which covers an install whose marker was
    /// lost).</summary>
    public static bool IsInstalled(string hooksTomlPath) {
        var dir = Path.GetDirectoryName(hooksTomlPath);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MarkerFileName))) return true;

        return TomlConfigFile.Read(hooksTomlPath) is { } root
            && MistralVibeHooksParser.HooksArray(root).Any(MistralVibeHooksParser.EntryReferencesCapacitorVibeHook);
    }

    /// <summary>Ensures exactly one kcap entry per <see cref="MistralVibeHooksParser.VibeHookTypes"/>
    /// exists, preserving every non-kcap entry. Writes the version marker on success.</summary>
    public static TomlConfigFile.Outcome Install(string hooksTomlPath) {
        var outcome = TomlConfigFile.Edit(hooksTomlPath, InstallMutate);
        if (outcome != TomlConfigFile.Outcome.Failed) WriteMarker(hooksTomlPath);
        return outcome;
    }

    /// <summary>Removes the kcap-owned entries, preserving user entries, and drops the array when it
    /// empties. Deletes the version marker.</summary>
    public static TomlConfigFile.Outcome Remove(string hooksTomlPath) {
        var outcome = TomlConfigFile.Edit(hooksTomlPath, RemoveMutate);
        DeleteMarker(hooksTomlPath);
        return outcome;
    }

    static bool InstallMutate(TomlTable root) {
        // A non-array `hooks` is not a shape kcap wrote — leave it rather than clobber user data.
        if (root.TryGetValue(MistralVibeHooksParser.HooksKey, out var existing) && existing is not TomlTableArray)
            return false;

        var array     = MistralVibeHooksParser.HooksArray(root);
        var kcapCount = array.Count(MistralVibeHooksParser.EntryReferencesCapacitorVibeHook);
        if (kcapCount == MistralVibeHooksParser.VibeHookTypes.Length &&
            MistralVibeHooksParser.HasCapacitorHooksFor(root, MistralVibeHooksParser.VibeHookTypes))
            return false; // already exactly our set

        var rebuilt = new TomlTableArray();
        foreach (var entry in array)
            if (!MistralVibeHooksParser.EntryReferencesCapacitorVibeHook(entry)) rebuilt.Add(entry);
        foreach (var type in MistralVibeHooksParser.VibeHookTypes)
            rebuilt.Add(MistralVibeHooksParser.BuildKcapEntry(type));

        root[MistralVibeHooksParser.HooksKey] = rebuilt;
        return true;
    }

    static bool RemoveMutate(TomlTable root) {
        if (!root.TryGetValue(MistralVibeHooksParser.HooksKey, out var v) || v is not TomlTableArray array)
            return false;

        var kept    = new TomlTableArray();
        var removed = false;
        foreach (var entry in array) {
            if (MistralVibeHooksParser.EntryReferencesCapacitorVibeHook(entry)) { removed = true; continue; }
            kept.Add(entry);
        }

        if (!removed) return false;
        if (kept.Count == 0) root.Remove(MistralVibeHooksParser.HooksKey);
        else root[MistralVibeHooksParser.HooksKey] = kept;
        return true;
    }

    public static string? ReadMarker(string hooksTomlPath) {
        var dir = Path.GetDirectoryName(hooksTomlPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    static void WriteMarker(string hooksTomlPath) {
        var dir = Path.GetDirectoryName(hooksTomlPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    static void DeleteMarker(string hooksTomlPath) {
        var dir = Path.GetDirectoryName(hooksTomlPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
    }
}
