namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>
/// Filesystem layout for Mistral Vibe CLI state. Everything lives under a single root:
/// <c>$VIBE_HOME</c> when set (it replaces <c>~/.vibe</c> wholesale, as Vibe's own home resolution
/// reads it first), otherwise <c>~/.vibe</c> on every OS. Hooks live in <c>hooks.toml</c> and MCP
/// servers in <c>config.toml</c> — both TOML, so the installers MERGE rather than overwrite (see
/// <see cref="MistralVibeHooksParser"/>, <see cref="MistralVibeConfigToml"/>).
/// </summary>
public sealed class MistralVibePaths {
    public MistralVibePaths(UserHome home, string? vibeHome) =>
        Home = !string.IsNullOrWhiteSpace(vibeHome) ? vibeHome : Path.Combine(home.Path, ".vibe");

    public string Home { get; }

    /// <summary>User-global hooks file (<c>~/.vibe/hooks.toml</c>) — holds the <c>[[hooks]]</c> array
    /// kcap merges its own entries into. NEVER overwrite wholesale.</summary>
    public string HooksToml => Path.Combine(Home, "hooks.toml");

    /// <summary>User config (<c>~/.vibe/config.toml</c>) — holds Vibe settings plus the MCP-server
    /// tables kcap registers into. Also the marker that <c>vibe --setup</c> has run.</summary>
    public string ConfigToml => Path.Combine(Home, "config.toml");

    /// <summary>Where Vibe writes session transcripts: <c>~/.vibe/logs/session</c>. Holds legacy
    /// <c>session_&lt;ts&gt;/</c> directories and the current <c>unified/&lt;id&gt;/</c> store.</summary>
    public string SessionLogsDir => Path.Combine(Home, "logs", "session");

    /// <summary>The unified-harness session store (<c>~/.vibe/logs/session/unified</c>), one
    /// <c>&lt;session-id&gt;/</c> directory per session.</summary>
    public string UnifiedDir => Path.Combine(SessionLogsDir, "unified");

    /// <summary>Whether the Vibe CLI has run here — a config file it writes or a session it logged.
    /// The bare root does not count: kcap can create <see cref="HooksToml"/> before Vibe ever runs,
    /// so that file is not a user-data marker.</summary>
    public bool HasUserData() =>
        Directory.Exists(Home)
     && (File.Exists(ConfigToml) || Directory.Exists(SessionLogsDir));
}
