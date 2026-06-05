using System.Reflection;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

public static class UpdateCommand {
    static readonly string CachePath = PathHelpers.ConfigPath("update-check.json");

    public static async Task<int> HandleAsync() {
        var (latest, current) = await CheckForUpdateAsync(forceCheck: true);

        if (latest is null) {
            await Console.Error.WriteLineAsync("Could not check for updates.");

            return 1;
        }

        if (!IsNewer(latest, current)) {
            await Console.Out.WriteLineAsync($"Already up to date: {current}");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Update available: {current} → {latest}");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("Run:");
        await Console.Out.WriteLineAsync("  npm update -g @kurrent/kcap");

        return 0;
    }

    /// <summary>
    /// Print an update hint to stderr if a newer version is available.
    /// Called on every CLI invocation (cached, max once per 24h).
    /// </summary>
    public static async Task PrintUpdateHintIfAvailable() {
        var config = await AppConfig.Load();

        if (config?.UpdateCheck == false) return;

        try {
            var (latest, current) = await CheckForUpdateAsync(forceCheck: false);

            if (latest is not null && current is not null && IsNewer(latest, current)) {
                await Console.Error.WriteLineAsync();
                await Console.Error.WriteLineAsync($"Update available: {current} → {latest}");
                await Console.Error.WriteLineAsync("Run `npm update -g @kurrent/kcap` to update");
            }
        } catch {
            // Best effort — never break the CLI for update checks
        }
    }

    static async Task<(string? latest, string? current)> CheckForUpdateAsync(bool forceCheck) {
        var current = GetCurrentVersion();

        if (!forceCheck) {
            // Check cache
            if (File.Exists(CachePath)) {
                try {
                    var cacheJson     = await File.ReadAllTextAsync(CachePath);
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
            var resp = await http.GetAsync("https://registry.npmjs.org/@kurrent/kcap/latest");

            if (!resp.IsSuccessStatusCode) return (null, current);

            var body   = await resp.Content.ReadAsStringAsync();
            var json   = JsonNode.Parse(body);
            var latest = json?["version"]?.GetValue<string>();

            // Cache result
            if (latest is not null) {
                var dir = Path.GetDirectoryName(CachePath)!;
                Directory.CreateDirectory(dir);

                var cacheObj = new JsonObject {
                    ["latest_version"] = latest,
                    ["checked_at"]     = DateTimeOffset.UtcNow
                };
                var tempPath = $"{CachePath}.tmp";
                await File.WriteAllTextAsync(tempPath, cacheObj.ToJsonString());
                File.Move(tempPath, CachePath, overwrite: true);
            }

            return (latest, current);
        } catch {
            return (null, current);
        }
    }

    static bool IsNewer(string? latest, string? current) => SemverCompare.IsNewer(latest, current);

    static string? GetCurrentVersion() =>
        typeof(UpdateCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
}
