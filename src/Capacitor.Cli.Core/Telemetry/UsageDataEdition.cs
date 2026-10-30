using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>What this machine's edition makes of a usage-data choice nobody has saved.</summary>
public static class UsageDataEdition {
    /// <summary>A local installation, a local setup in flight, or local state that cannot be read, which may be either.</summary>
    public static bool IsLocal(LocalRoot root) =>
        LocalSetupState.Pending(root)
     || LocalStateFiles.ReadRegistry(root) is StateRead<InstallRegistry>.Present { Value.Active: not null } or StateRead<InstallRegistry>.Unreadable;

    /// <summary>A profile for a server other than the local installation, which also keeps the local edition from
    /// being set up here. A config that cannot be read is not one.</summary>
    public static bool HasCloudProfile(ConfigRoot config, LocalRoot root) =>
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var profiles)
     && LocalSetupState.HasForeignServerProfile(profiles, LocalEndpoints.Read(root));

    /// <summary>The desktop app's: a machine already set up for a cloud workspace keeps the notice and opt-out; every
    /// other machine waits, for the wizard's edition and, in the local edition, for the user's answer.</summary>
    public static UnsavedConsent ForDesktop(ConfigRoot config, LocalRoot root, bool localEditionSupported) =>
        IsLocal(root)                                             ? UnsavedConsent.Pending
      : !localEditionSupported || HasCloudProfile(config, root) ? UnsavedConsent.Share
      : UnsavedConsent.Pending;
}
