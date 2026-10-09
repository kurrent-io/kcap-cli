using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Directories that look like a vendor's config dir. Presence of the vendor's own files
/// only — contents are never opened, since some of them hold credentials. Never throws: unreadable
/// directories or bad paths are skipped.</summary>
public static class AccountDiscovery {
    public static IReadOnlyList<AccountCandidate> Find(UserHome home, AccountRegistry registry, Func<string, string?> env) {
        var found = new List<AccountCandidate>();

        foreach (var dir in Children(home.Path, ".claude")) if (LooksLikeClaude(dir)) found.Add(new(HarnessId.Claude, dir, "home directory"));
        foreach (var dir in Children(home.Path, ".codex"))  if (LooksLikeCodex(dir))  found.Add(new(HarnessId.Codex, dir, "home directory"));

        if (ClaudeSwapBase(home, env) is { } swap && Directory.Exists(Path.Combine(swap, "sessions")))
            foreach (var dir in SafeEnumerateDirectories(Path.Combine(swap, "sessions")))
                if (LooksLikeClaude(dir)) found.Add(new(HarnessId.Claude, dir, "claude-swap profile"));

        if (env("CLAUDE_CONFIG_DIR") is { Length: > 0 } c && LooksLikeClaude(c) && TryNormalize(c) is { } nc) found.Add(new(HarnessId.Claude, nc, "CLAUDE_CONFIG_DIR"));
        if (env("CODEX_HOME") is { Length: > 0 } x && LooksLikeCodex(x) && TryNormalize(x) is { } nx)         found.Add(new(HarnessId.Codex, nx, "CODEX_HOME"));

        var result = new List<AccountCandidate>();
        foreach (var candidate in found) {
            var normalized = candidate with { Directory = AccountDirectory.Normalize(candidate.Directory) };
            if (registry.Accounts.Any(a => a.Vendor == normalized.Vendor && AccountDirectory.Same(a.Directory, normalized.Directory))) continue;
            if (result.Any(r => r.Vendor == normalized.Vendor && AccountDirectory.Same(r.Directory, normalized.Directory))) continue;
            result.Add(normalized);
        }

        return [.. result.OrderBy(c => c.Vendor).ThenBy(c => c.Directory, StringComparer.Ordinal)];
    }

    static IEnumerable<string> Children(string root, string prefix) {
        if (!Directory.Exists(root)) return [];
        try {
            return Directory.EnumerateDirectories(root, prefix + "*", SearchOption.TopDirectoryOnly).ToList();
        } catch (UnauthorizedAccessException) {
            return [];
        } catch (IOException) {
            return [];
        }
    }

    static bool LooksLikeClaude(string dir) =>
        File.Exists(Path.Combine(dir, "settings.json")) || Directory.Exists(Path.Combine(dir, "projects"));

    static bool LooksLikeCodex(string dir) =>
        File.Exists(Path.Combine(dir, "config.toml")) || Directory.Exists(Path.Combine(dir, "sessions"));

    static string? ClaudeSwapBase(UserHome home, Func<string, string?> env) {
        if (!OperatingSystem.IsLinux()) return Path.Combine(home.Path, ".claude-swap-backup");
        return env("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? Path.Combine(xdg, "claude-swap")
            : Path.Combine(home.Path, ".local", "share", "claude-swap");
    }

    static IEnumerable<string> SafeEnumerateDirectories(string path) {
        try {
            return Directory.EnumerateDirectories(path).ToList();
        } catch (UnauthorizedAccessException) {
            return [];
        } catch (IOException) {
            return [];
        }
    }

    static string? TryNormalize(string path) {
        try {
            return AccountDirectory.Normalize(path);
        } catch (ArgumentException) {
            return null;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }
}
