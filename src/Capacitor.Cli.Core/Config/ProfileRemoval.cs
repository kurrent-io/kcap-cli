using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Config;

/// Removes a profile, its bindings and its credential. The one removal both
/// <c>kcap profile remove</c> and the desktop app run.
public static class ProfileRemoval {
    public static async Task<ProfileRemovalResult> RemoveAsync(
            ConfigRoot config, TokenStore tokens, string name, CancellationToken ct = default) {
        if (name == ProfileConfig.DefaultName) return new(ProfileRemovalOutcome.IsDefault);

        // Decided on the locked config: a concurrent `kcap use --global` cannot make the profile
        // active between the check and the write.
        try {
            await ConfigMutator.MutateStrictAsync(config, current => {
                if (!current.Profiles.ContainsKey(name)) throw new Refusal(ProfileRemovalOutcome.NotFound);
                if (current.ActiveName == name) throw new Refusal(ProfileRemovalOutcome.IsActive);

                var profiles = new Dictionary<string, Profile>(current.Profiles);
                profiles.Remove(name);
                var bindings = current.ProfileBindings
                    .Where(kv => kv.Value != name)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                return current with { Profiles = profiles, ProfileBindings = bindings };
            }, ct);
        } catch (Refusal refusal) {
            return new(refusal.Outcome);
        } catch (ConfigUnreadableException) {
            return new(ProfileRemovalOutcome.ConfigUnreadable);
        }

        // Deleted only when no remaining profile can read the file: a same-name recreation, or a
        // case-alias on a case-insensitive filesystem, owns it now.
        try {
            var outcome = await tokens.DeleteGuardedAsync(name,
                cfg => !cfg.Profiles.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)), ct);

            return outcome == GuardedWriteOutcome.ConfigUnreadable
                ? new(ProfileRemovalOutcome.RemovedTokenRetained, "config unreadable")
                : new(ProfileRemovalOutcome.Removed);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
            return new(ProfileRemovalOutcome.RemovedTokenRetained, $"{tokens.TokenPath(name)}: {ex.Message}");
        }
    }

    sealed class Refusal(ProfileRemovalOutcome outcome) : Exception {
        public ProfileRemovalOutcome Outcome { get; } = outcome;
    }
}
