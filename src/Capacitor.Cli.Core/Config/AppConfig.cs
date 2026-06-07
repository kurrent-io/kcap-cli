using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Config;

public record CapacitorConfig {
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonSettings? Daemon { get; init; }

    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; init; } = true;

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; init; } = "org_public";

    [JsonPropertyName("excluded_repos")]
    public string[] ExcludedRepos { get; init; } = [];
}

public record DaemonSettings {
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("max_agents")]
    public int MaxAgents { get; init; } = 5;

    [JsonPropertyName("claude_path")]
    public string? ClaudePath { get; init; }

    [JsonPropertyName("codex_path")]
    public string? CodexPath { get; init; }
}

[JsonSerializable(typeof(CapacitorConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(CapacitorConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ConfigJsonContextIndented : JsonSerializerContext;

public static class AppConfig {
    static readonly string ConfigPath = PathHelpers.ConfigPath("config.json");

    public static string? ResolvedServerUrl { get; private set; }

    public static ResolvedProfile? ResolvedProfile { get; private set; }

    public static string RepoRoot => GetGitRepoRoot() ?? Environment.CurrentDirectory;

    /// <summary>
    /// Resolve server URL using only the active profile (or KCAP_PROFILE /
    /// KCAP_URL / --server-url overrides). Skips repo discovery and git
    /// remote matching — used by the daemon, which is not bound to a working
    /// directory.
    /// </summary>
    public static async Task<string?> ResolveActiveProfile(string[] args) {
        var idx          = Array.IndexOf(args, "--server-url");
        var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
        var envUrl       = Environment.GetEnvironmentVariable("KCAP_URL");
        var envProfile   = Environment.GetEnvironmentVariable("KCAP_PROFILE");

        var config   = await LoadProfileConfig();
        var resolver = new ProfileResolver(
            config,
            cliServerUrl,
            envUrl,
            envProfile,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var resolved = resolver.Resolve();
        ResolvedProfile   = resolved;
        ResolvedServerUrl = resolved.ServerUrl;

        if (resolved.Warning is not null) {
            await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
        }

        return resolved.ServerUrl;
    }

    public static async Task<string?> ResolveServerUrl(string[] args) {
        var idx          = Array.IndexOf(args, "--server-url");
        var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;

        var envUrl     = Environment.GetEnvironmentVariable("KCAP_URL");
        var envProfile = Environment.GetEnvironmentVariable("KCAP_PROFILE");

        // Short-circuit: if explicit URL is provided, skip all profile/repo resolution
        if (cliServerUrl is not null || envUrl is not null) {
            var config = await LoadProfileConfig();

            var resolver = new ProfileResolver(
                config,
                cliServerUrl,
                envUrl,
                envProfile,
                repoConfig: null,
                repoRemoteUrls: [],
                repoPath: null
            );
            var quickResolved = resolver.Resolve();
            ResolvedProfile   = quickResolved;
            ResolvedServerUrl = quickResolved.ServerUrl;

            return quickResolved.ServerUrl;
        }

        {
            var config = await LoadProfileConfig();

            var repoRoot = RepoRoot;

            RepoConfig? repoConfig     = null;
            var         repoConfigPath = Path.Combine(repoRoot, ".kcap.json");

            if (File.Exists(repoConfigPath)) {
                try {
                    var json = await File.ReadAllTextAsync(repoConfigPath);
                    repoConfig = JsonSerializer.Deserialize(json, RepoConfigJsonContext.Default.RepoConfig);
                } catch {
                    /* ignore malformed */
                }
            }

            var remoteUrls = GetGitRemoteUrls();

            var resolver = new ProfileResolver(
                config,
                cliServerUrl,
                envUrl,
                envProfile,
                repoConfig,
                remoteUrls,
                repoRoot
            );

            var resolved = resolver.Resolve();
            ResolvedProfile   = resolved;
            ResolvedServerUrl = resolved.ServerUrl;

            if (resolved.Warning is not null) {
                await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
            }

            return resolved.ServerUrl;
        }
    }

    static string[] GetGitRemoteUrls() {
        try {
            var psi = new ProcessStartInfo("git", "remote -v") {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);

            if (proc is null) return [];

            var output = proc.StandardOutput.ReadToEnd();

            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(); } catch {
                    /* best effort */
                }

                return [];
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', ' ').ElementAtOrDefault(1))
                .Where(url => url is not null)
                .Distinct()
                .ToArray()!;
        } catch {
            return [];
        }
    }

    static string? GetGitRepoRoot() {
        try {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel") {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);

            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();

            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(); } catch {
                    /* best effort */
                }

                return null;
            }

            return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Removes trailing slashes from a URL to prevent double-slash issues
    /// when appending paths (e.g., <c>https://example.com/</c> + <c>/auth/config</c>).
    /// </summary>
    public static string NormalizeUrl(string url) => url.TrimEnd('/');

    static readonly string[] ValidVisibilities = ["private", "org_public", "public"];

    public static async Task<CapacitorConfig?> Load() {
        if (!File.Exists(ConfigPath))
            return null;

        try {
            var json   = await File.ReadAllTextAsync(ConfigPath);
            var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CapacitorConfig);

            if (config is null) return null;

            // Normalize default_visibility to lowercase; reset invalid values to default
            var vis = (config.DefaultVisibility ?? "org_public").ToLowerInvariant();

            if (!ValidVisibilities.Contains(vis)) vis = "org_public";

            return vis == config.DefaultVisibility ? config : config with { DefaultVisibility = vis };
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Warning: could not read config at {ConfigPath}: {ex.Message}");

            return null;
        }
    }

    public static async Task<ProfileConfig> LoadProfileConfig() {
        if (!File.Exists(ConfigPath))
            return new() { Profiles = new() { ["default"] = new() } };

        string json;

        try {
            json = await File.ReadAllTextAsync(ConfigPath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Warning: could not read config at {ConfigPath}: {ex.Message}");

            return new() { Profiles = new() { ["default"] = new() } };
        }

        ConfigMigration.MigrationResult result;

        try {
            result = ConfigMigration.MigrateIfNeeded(json);
        } catch (JsonException ex) {
            await Console.Error.WriteLineAsync($"Warning: invalid config at {ConfigPath}: {ex.Message}");

            return new() { Profiles = new() { ["default"] = new() } };
        }

        // Persist a v1→v2 migration when possible, but never drop the in-memory
        // migrated config if the write fails (e.g. read-only volume). Losing the
        // server URL here previously caused `ServerUrl is required` at daemon
        // startup despite the on-disk config being intact.
        if (result.ShouldPersist) {
            try {
                await SaveProfileConfig(result.Config);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync($"Warning: could not persist migrated config at {ConfigPath}: {ex.Message}");
            }
        }

        return NormalizeProfileVisibilities(result.Config);
    }

    /// <summary>
    /// Coerce each profile's <c>default_visibility</c> to the same set the
    /// legacy <see cref="Load"/> path enforced (lowercase, restricted to
    /// <see cref="ValidVisibilities"/>; fall back to <c>org_public</c>
    /// otherwise). Manual edits and v1→v2 migrations bypass the validation
    /// that <c>kcap config set</c> / <c>kcap setup</c> apply at write time,
    /// so a profile on disk can carry through values like <c>"Private"</c>
    /// or <c>"foo"</c> that the server would reject.
    /// </summary>
    static ProfileConfig NormalizeProfileVisibilities(ProfileConfig config) {
        Dictionary<string, Profile>? rebuilt = null;

        foreach (var (name, profile) in config.Profiles) {
            var raw        = profile.DefaultVisibility ?? "org_public";
            var normalized = raw.ToLowerInvariant();

            if (!ValidVisibilities.Contains(normalized)) normalized = "org_public";

            if (normalized == profile.DefaultVisibility) continue;

            rebuilt                                                 ??= new(config.Profiles);
            rebuilt[name] = profile with { DefaultVisibility = normalized };
        }

        return rebuilt is null ? config : config with { Profiles = rebuilt };
    }

    public static async Task SaveProfileConfig(ProfileConfig config) {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{ConfigPath}.tmp";

        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig)
        );
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static string GetConfigPath() => ConfigPath;

    /// <summary>
    /// Returns the profile whose per-profile settings apply to the current
    /// process. Prefers <paramref name="resolvedProfile"/> (set by
    /// <see cref="ResolveServerUrl"/>) and falls back to the on-disk
    /// <see cref="ProfileConfig.ActiveProfile"/> when URL overrides
    /// (<c>--server-url</c> / <c>KCAP_URL</c>) caused the resolver to
    /// skip profile selection. Exposed for testing; production callers should
    /// use <see cref="GetActiveProfileAsync"/>.
    /// </summary>
    public static Profile? PickActiveProfile(Profile? resolvedProfile, ProfileConfig fallback) =>
        resolvedProfile ?? fallback.Profiles.GetValueOrDefault(fallback.ActiveProfile);

    /// <summary>
    /// Convenience wrapper around <see cref="PickActiveProfile"/> that pulls
    /// the resolved profile from <see cref="ResolvedProfile"/> and loads the
    /// fallback config from disk when needed.
    /// </summary>
    public static async Task<Profile?> GetActiveProfileAsync() {
        if (ResolvedProfile?.Profile is { } profile) return profile;

        return PickActiveProfile(null, await LoadProfileConfig());
    }
}
