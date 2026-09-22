using System.Text.Json;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using RepoConfigJsonContextIndented = Capacitor.Cli.Core.Config.RepoConfigJsonContextIndented;

namespace Capacitor.Cli.Commands;

public sealed class UseCommand(ConfigRoot config, WorkingDirectory workdir, TokenStore tokens) {
    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kcap use <profile-name> [--global] [--save]");
            return 1;
        }

        var name = args[1];
        var global = args.Contains("--global");
        var save = args.Contains("--save");
        // Resolved at most once, and not at all for a global selection that saves nothing:
        // RepoRootOf shells out to git, which a change needing no repository must not wait on.
        var repoRoot = !global || save ? AppConfig.RepoRootOf(workdir) : null;
        var repoPath = global ? null : repoRoot;

        return await SetProfile(name, repoPath, global, save, save ? repoRoot : null);
    }

    internal async Task<int> SetProfile(
        string name, string? repoPath, bool global, bool save, string? savePath
    ) {
        var selectsGlobally = global || repoPath is null;

        // The outgoing active profile's legacy credential is settled before the name it follows moves.
        if (selectsGlobally) {
            if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var before)) {
                await Console.Error.WriteLineAsync("The configuration file could not be read; nothing was changed.");
                return 1;
            }
            try {
                await tokens.MigrateLegacyAsync(before.ActiveName);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
                await Console.Error.WriteLineAsync(
                    $"Could not move the saved sign-in of profile '{before.ActiveName}' ({ex.Message}); nothing was changed.");
                return 1;
            }
        }

        Profile? profile = null;
        try {
            await ConfigMutator.MutateStrictAsync(config, c => {
                if (!c.Profiles.TryGetValue(name, out profile)) throw new UnknownProfile();
                return selectsGlobally
                    ? c with { ActiveProfile = name }
                    : c with { ProfileBindings = new Dictionary<string, string>(c.ProfileBindings) { [repoPath!] = name } };
            });
        } catch (UnknownProfile) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found. Run `kcap profile list` to see available profiles.");
            return 1;
        } catch (ConfigUnreadableException) {
            await Console.Error.WriteLineAsync("The configuration file could not be read; nothing was changed.");
            return 1;
        }

        await Console.Out.WriteLineAsync(selectsGlobally
            ? $"Active profile set to '{name}' (global)."
            : $"Profile '{name}' bound to {repoPath}.");

        if (save && savePath is not null) {
            var repoConfig = new RepoConfig {
                Profile = name,
                ServerUrl = profile!.ServerUrl
            };
            var repoConfigPath = Path.Combine(savePath, ".kcap.json");
            await File.WriteAllBytesAsync(repoConfigPath,
                JsonSerializer.SerializeToUtf8Bytes(repoConfig, RepoConfigJsonContextIndented.Default.RepoConfig));
            await Console.Out.WriteLineAsync($"Wrote {repoConfigPath} — commit this to share with your team.");
        }

        return 0;
    }

    sealed class UnknownProfile : Exception;
}
