using System.Reflection;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

public static class UpdateCommand {
    /// <summary>
    /// npm registry base URL. Overridable seam so integration tests can point
    /// the CLI at a fake registry (e.g. WireMock) instead of the real npm registry.
    /// </summary>
    internal static string RegistryBaseUrl = "https://registry.npmjs.org";

    /// <summary>Valid npm dist-tags for the update channel (Phase 1).</summary>
    static readonly string[] KnownChannels = ["latest", "beta"];

    /// <summary>
    /// Resolves the effective update channel (npm dist-tag): an explicit
    /// <c>--beta</c>/<c>--stable</c> flag wins, otherwise the configured channel
    /// is used. The result is validated against <see cref="KnownChannels"/> and
    /// falls back to <c>"latest"</c> for anything unset/blank/unknown. This is the
    /// single trust boundary for the channel value: it flows into a filesystem
    /// cache path (<c>update-check-{channel}.json</c>) and the registry URL, so a
    /// corrupted or hand-edited <c>update_channel</c> (a typo, or one containing
    /// <c>..</c> or a rooted path) must never reach either unsanitized. STJ
    /// source-gen also doesn't apply the record default on deserialize, so a real
    /// profile can carry a null <c>UpdateChannel</c> — also handled here.
    /// </summary>
    internal static string ResolveChannel(string[] args, string? configuredChannel) {
        if (args.Contains("--stable")) return "latest";
        if (args.Contains("--beta"))   return "beta";
        var channel = configuredChannel?.Trim().ToLowerInvariant();
        return channel is not null && KnownChannels.Contains(channel) ? channel : "latest";
    }

    public static async Task<int> HandleAsync(string[] args) {
        var profile   = await AppConfig.GetActiveProfileAsync();
        var channel   = ResolveChannel(args, profile?.UpdateChannel);
        var checkOnly = args.Contains("--check");

        // Persist an explicit channel switch onto the active profile so future
        // auto-updates track it. Update the profile inside ProfileConfig and save
        // the whole v2 config via SaveProfileConfig — NEVER write a flat
        // LegacyV1Config, which would overwrite the user's v2 profile config.
        if (args.Contains("--beta") || args.Contains("--stable")) {
            var pc = await AppConfig.LoadProfileConfig();

            // Must match the profile GetActiveProfileAsync() resolved above (the
            // read side): when a profile was resolved (env var, .kcap.json, git
            // remote match, or `kcap use` binding), persist to THAT profile —
            // not blindly to the on-disk active_profile — so the switch sticks
            // for the profile the user is actually on.
            var targetName = AppConfig.ResolvedProfile?.ProfileName ?? pc.ActiveProfile;
            if (pc.Profiles.TryGetValue(targetName, out var active)
             && active.UpdateChannel != channel) {
                var profiles = new Dictionary<string, Profile>(pc.Profiles) {
                    [targetName] = active with { UpdateChannel = channel }
                };
                await AppConfig.SaveProfileConfig(pc with { Profiles = profiles });
            }
        }

        var (latest, current) = await CheckForUpdateAsync(forceCheck: true, channel);

        if (checkOnly) {
            // Machine-readable probe consumed by the npm launcher (kcap.js).
            // One JSON line on stdout; exit 1 only when the check itself failed.
            //
            // `newer` is a tri-state: true => upgrade, false => confidently up to
            // date, null => can't tell (current version unknown or registry check
            // failed). The launcher must NOT skip on null — otherwise a binary
            // that reports "unknown" would strand the user on a stale CLI.
            bool? newer = string.IsNullOrEmpty(current) || latest is null
                ? null
                : IsNewer(latest, current);

            var obj = new JsonObject {
                ["current"]     = current,
                ["latest"]      = latest,
                ["newer"]       = newer,
                ["channel"]     = channel,
                ["install_tag"] = channel,
            };

            await Console.Out.WriteLineAsync(obj.ToJsonString());

            return latest is null ? 1 : 0;
        }

        if (latest is null) {
            await Console.Error.WriteLineAsync("Could not check for updates.");

            return 1;
        }

        if (string.IsNullOrEmpty(current)) {
            await Console.Error.WriteLineAsync($"Could not determine the current kcap version. Latest published: {latest}.");

            return 1;
        }

        if (!IsNewer(latest, current)) {
            await Console.Out.WriteLineAsync($"Already up to date: {current}");

            return 0;
        }

        // Reached only when the native binary is run WITHOUT the npm launcher
        // (e.g. invoking the platform binary directly). For npm-global installs
        // the launcher intercepts `update` and performs the upgrade itself.
        await Console.Out.WriteLineAsync($"Update available: {current} → {latest}");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("Run `kcap update` to update, or upgrade directly:");
        await Console.Out.WriteLineAsync("  npm install -g @kurrent/kcap@latest");

        return 0;
    }

    /// <summary>
    /// Print an update hint to stderr if a newer version is available.
    /// Called on every CLI invocation (cached, max once per 24h).
    /// </summary>
    public static async Task PrintUpdateHintIfAvailable() {
        try {
            var profile = await AppConfig.GetActiveProfileAsync();
            if (profile?.UpdateCheck == false) return;
            var channel = ResolveChannel([], profile?.UpdateChannel);
            var (latest, current) = await CheckForUpdateAsync(forceCheck: false, channel);

            if (latest is not null && current is not null && IsNewer(latest, current)) {
                await Console.Error.WriteLineAsync();
                await Console.Error.WriteLineAsync($"Update available: {current} → {latest}");
                await Console.Error.WriteLineAsync("Run `kcap update` to update");
            }
        } catch {
            // Best effort — never break the CLI for update checks
        }
    }

    /// <summary>
    /// Per-channel cache path so a <c>beta</c> check doesn't clobber the
    /// cached <c>latest</c> result (and vice versa).
    /// </summary>
    static string CachePathFor(string channel) =>
        PathHelpers.ConfigPath($"update-check-{channel}.json");

    internal static async Task<(string? latest, string? current)> CheckForUpdateAsync(bool forceCheck, string channel) {
        var current   = GetCurrentVersion();
        var cachePath = CachePathFor(channel);

        if (!forceCheck) {
            // Check cache
            if (File.Exists(cachePath)) {
                try {
                    var cacheJson     = await File.ReadAllTextAsync(cachePath);
                    var cache         = JsonNode.Parse(cacheJson);
                    var checkedAt     = cache?["checked_at"]?.GetValue<DateTimeOffset>();
                    var cachedVersion = cache?["latest_version"]?.GetValue<string>();

                    if (checkedAt is not null
                     && DateTimeOffset.UtcNow - checkedAt.Value < TimeSpan.FromHours(24)
                     && cachedVersion is not null) {
                        return (cachedVersion, current);
                    }
                } catch {
                    // Corrupted cache — re-check
                }
            }
        }

        // Query npm registry
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        http.DefaultRequestHeaders.Add("User-Agent", "kcap-cli");

        try {
            var resp = await http.GetAsync($"{RegistryBaseUrl}/@kurrent/kcap/{channel}");

            if (!resp.IsSuccessStatusCode) return (null, current);

            var body   = await resp.Content.ReadAsStringAsync();
            var json   = JsonNode.Parse(body);
            var latest = json?["version"]?.GetValue<string>();

            // Cache result
            if (latest is not null) {
                var dir = Path.GetDirectoryName(cachePath)!;
                Directory.CreateDirectory(dir);

                var cacheObj = new JsonObject {
                    ["latest_version"] = latest,
                    ["checked_at"]     = DateTimeOffset.UtcNow
                };
                var tempPath = $"{cachePath}.tmp";
                await File.WriteAllTextAsync(tempPath, cacheObj.ToJsonString());
                File.Move(tempPath, cachePath, overwrite: true);
            }

            return (latest, current);
        } catch {
            return (null, current);
        }
    }

    static bool IsNewer(string? latest, string? current) => PrereleaseSemver.IsNewer(latest, current);

    static string? GetCurrentVersion() {
        var v = CapacitorVersion.CurrentDisplay();
        return v == "unknown" ? null : v;
    }
}
