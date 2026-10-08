namespace Capacitor.Cli.Services;

/// <summary>What <see cref="LaunchdServiceManager.RefreshUnit"/> did to one installed job.</summary>
enum UnitRefresh {
    /// <summary>Neither the plist nor the loaded job is out of date.</summary>
    Unchanged,

    /// <summary>The plist was rewritten; the loaded job already matched it, so no reload was needed.</summary>
    Rewritten,

    /// <summary>The loaded job is still out of date because the daemon was busy.</summary>
    Deferred,

    /// <summary><c>launchctl print</c> could not be read, so whether the job needs a reload is unknown.
    /// The plist is made current either way.</summary>
    Unverified,

    /// <summary>The job was reloaded from the current plist.</summary>
    Reloaded,

    /// <summary>The reload failed; the error says what launchd is left holding.</summary>
    Failed,
}
