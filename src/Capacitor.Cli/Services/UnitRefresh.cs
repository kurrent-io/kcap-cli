namespace Capacitor.Cli.Services;

/// <summary>What <see cref="LaunchdServiceManager.RefreshUnit"/> found or did for one installed job.</summary>
enum UnitRefresh {
    /// <summary>The plist declares Standard and the loaded job's spawn type reads positive.</summary>
    Current,

    /// <summary>The plist is current or was rewritten, but the label is not loaded.</summary>
    NotLoaded,

    /// <summary>The loaded job is still out of date: the daemon refused the restart, or there was not enough
    /// time left to finish a reload.</summary>
    Deferred,

    /// <summary>Another service operation holds the label's transaction lock.</summary>
    Contended,

    /// <summary>launchd's state could not be read, or the loaded spawn type is a word that proves nothing.</summary>
    Unverified,

    /// <summary>No plist at the path.</summary>
    UnitMissing,

    /// <summary>The plist exists but cannot be read.</summary>
    UnitUnreadable,

    /// <summary>The plist cannot be parsed, or does not declare Standard after the attempted upgrade.</summary>
    UnitUnsupported,

    /// <summary>The job was reloaded from the plist on disk and now reads a positive spawn type.</summary>
    Reloaded,

    /// <summary>The reload failed; the error says what launchd is left holding.</summary>
    Failed,
}
