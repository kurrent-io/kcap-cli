using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services;

/// Edits only the profile the daemon graph is attached to, preserving concurrent config changes.
public sealed class SettingsProfileStore(ConfigRoot config, string profileName, string serverUrl) {
    public string ProfileName => profileName;
    public string ServerUrl => serverUrl;

    public DaemonSettings Load() {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var current))
            throw new InvalidOperationException("Could not read the profile settings.");
        return Profile(current).Daemon ?? new DaemonSettings();
    }

    public Task SaveCapacityAsync(int capacity, CancellationToken ct) =>
        SaveAsync(settings => settings with { MaxAgents = capacity }, ct);

    public Task SaveNameAsync(string name, CancellationToken ct) =>
        SaveAsync(settings => settings with { Name = name }, ct);

    Task SaveAsync(Func<DaemonSettings, DaemonSettings> update, CancellationToken ct) =>
        ConfigMutator.MutateAsync(config, current => {
            var profile = Profile(current);
            return current with {
                Profiles = new Dictionary<string, Profile>(current.Profiles) {
                    [profileName] = profile with { Daemon = update(profile.Daemon ?? new DaemonSettings()) }
                }
            };
        }, ct);

    Profile Profile(ProfileConfig current) {
        if (!current.Profiles.TryGetValue(profileName, out var profile) ||
            !ServerIdentity.Matches(profile.ServerUrl, serverUrl))
            throw new InvalidOperationException("The configured profile changed. Restart the app before editing settings.");
        return profile;
    }
}
