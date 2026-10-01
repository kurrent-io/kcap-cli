namespace Capacitor.Cli.Services;

/// <summary>What <see cref="LaunchdServiceManager.RefreshProcessType"/> did to one installed job.</summary>
enum ProcessTypeRefresh {
    /// <summary>Neither the plist nor the loaded job is Adaptive.</summary>
    Unchanged,

    /// <summary>The plist was rewritten; the job was not loaded as Adaptive, so no reload was needed.</summary>
    Rewritten,

    /// <summary>The job is still loaded as Adaptive because the daemon was busy.</summary>
    Deferred,

    /// <summary><c>launchctl print</c> could not be read, so whether the job needs a reload is unknown.
    /// The plist is made current either way.</summary>
    Unverified,

    /// <summary>The job was reloaded and now runs as Standard.</summary>
    Reloaded,

    /// <summary>The reload failed. The original plist is restored and bootstrapped again; the error says
    /// whether that rollback loaded.</summary>
    Failed,
}
