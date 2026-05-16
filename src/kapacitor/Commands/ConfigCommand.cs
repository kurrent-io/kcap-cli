using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ConfigCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kapacitor config <show|set> [key] [value]");

            return 1;
        }

        var subcommand = args[1];
        var skipProbe  = args.Contains("--no-probe");

        return subcommand switch {
            "show"                      => await Show(),
            "set" when args.Length >= 4 => await Set(args[2], args[3], skipProbe),
            "set"                       => SetUsage(),
            _                           => UnknownSubcommand(subcommand)
        };
    }

    static async Task<int> Show() {
        var profileConfig = await AppConfig.LoadProfileConfig();
        var json          = JsonSerializer.Serialize(profileConfig, ProfileConfigJsonContextIndented.Default.ProfileConfig);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath()}");

        return 0;
    }

    static async Task<int> Set(string key, string value, bool skipProbe) {
        if (key == "server_url") {
            var result = await ServerUrlNormalizer.NormalizeAsync(
                value, skipProbe, CancellationToken.None);

            if (result.Warning is not null)
                await Console.Error.WriteLineAsync($"Warning: {result.Warning}");

            value = result.Url;
        }

        var profileConfig = await AppConfig.LoadProfileConfig();
        var profileName   = profileConfig.ActiveProfile;
        var profile       = profileConfig.Profiles.GetValueOrDefault(profileName) ?? new Profile();

        profile = ApplySet(profile, key, value);

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) { [profileName] = profile };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        await Console.Out.WriteLineAsync($"Set {key} = {value} (profile: {profileName})");

        return 0;
    }

    /// <summary>
    /// Applies a single <c>key = value</c> update to a <see cref="Profile"/>. Pure function, exposed for testing.
    /// Throws <see cref="ArgumentException"/> on unknown keys or invalid values.
    /// </summary>
    public static Profile ApplySet(Profile profile, string key, string value) =>
        key switch {
            "server_url" => profile with { ServerUrl = value },
            "daemon.name" => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { Name = value } },
            "daemon.max_agents" when int.TryParse(value, out var n) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { MaxAgents = n } },
            "daemon.claude_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { ClaudePath = value } },
            "daemon.claude_path" => throw new ArgumentException("Invalid value for daemon.claude_path: must not be empty."),
            "daemon.codex_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { CodexPath = value } },
            "daemon.codex_path" => throw new ArgumentException("Invalid value for daemon.codex_path: must not be empty."),
            "update_check" when bool.TryParse(value, out var b) => profile with { UpdateCheck = b },
            "update_check" => throw new ArgumentException($"Invalid value for update_check: '{value}'. Must be true or false."),
            "disable_session_guidelines" when bool.TryParse(value, out var b) => profile with { DisableSessionGuidelines = b },
            "disable_session_guidelines" => throw new ArgumentException($"Invalid value for disable_session_guidelines: '{value}'. Must be true or false."),
            "default_visibility" when value is "private" or "org_public" or "public" => profile with { DefaultVisibility = value },
            "default_visibility" => throw new ArgumentException("Invalid value. Must be: private, org_public, or public"),
            "excluded_repos" => profile with { ExcludedRepos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
            _ => throw new ArgumentException($"Unknown config key: {key}")
        };

    static int SetUsage() {
        Console.Error.WriteLine("Usage: kapacitor config set <key> <value> [--no-probe]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Keys:");
        Console.Error.WriteLine("  server_url                  Server URL");
        Console.Error.WriteLine("  daemon.name                 Daemon name");
        Console.Error.WriteLine("  daemon.max_agents           Max concurrent hosted coding agents");
        Console.Error.WriteLine("  daemon.claude_path          Path to claude binary (default: claude)");
        Console.Error.WriteLine("  daemon.codex_path           Path to codex binary (default: codex)");
        Console.Error.WriteLine("  update_check                Enable update check (true/false)");
        Console.Error.WriteLine("  default_visibility          Default session visibility (private, org_public, public)");
        Console.Error.WriteLine("  disable_session_guidelines  Skip injecting recurring-lessons context at SessionStart (true/false)");
        Console.Error.WriteLine("  excluded_repos              Excluded repos, comma-separated (owner/repo,owner/repo)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Flags:");
        Console.Error.WriteLine("  --no-probe                  Skip the reachability check when setting server_url");

        return 1;
    }

    static int UnknownSubcommand(string subcommand) {
        Console.Error.WriteLine($"Unknown config subcommand: {subcommand}");

        return 1;
    }
}
