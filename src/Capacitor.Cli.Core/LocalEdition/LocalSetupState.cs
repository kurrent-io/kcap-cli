using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What the lifecycle journal and the profiles say about whether a local setup is finished.</summary>
public static class LocalSetupState {
    /// <summary>The journal operation name of a local setup.</summary>
    public const string Operation = "setup";

    /// <summary>True while a setup entry stands, and for a journal that cannot be read: an unreadable
    /// journal is not evidence that nothing was in flight.</summary>
    public static bool Pending(LocalRoot root) =>
        LocalStateFiles.ReadJournal(root) switch {
            StateRead<LifecycleJournalEntry>.Present(var entry) => entry.Operation == Operation,
            StateRead<LifecycleJournalEntry>.Unreadable         => true,
            _                                                   => false,
        };

    public static bool HasForeignServerProfile(ProfileConfig config, LocalEndpoints endpoints) =>
        FindForeignServerProfile(config, endpoints) is not null;

    /// <summary>The first profile, active or not, whose server is neither blank nor this machine's
    /// local installation.</summary>
    public static KeyValuePair<string, Profile>? FindForeignServerProfile(ProfileConfig config, LocalEndpoints endpoints) {
        foreach (var pair in config.Profiles) {
            if (string.IsNullOrWhiteSpace(pair.Value.ServerUrl)) continue;
            if (endpoints.Classify(pair.Value.ServerUrl) is LocalEndpointClass.Local) continue;

            return pair;
        }

        return null;
    }
}
