namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>Claude Code as this process sees it.</summary>
public sealed class ClaudeHarness : IHarness<ClaudeHarness> {
    ClaudeHarness(ClaudePaths paths) => Paths = paths;

    /// <summary>Resolves Claude's one override, <c>CLAUDE_CONFIG_DIR</c>, which replaces
    /// <c>~/.claude</c> wholesale.</summary>
    public static ClaudeHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static ClaudeHarness Over(ClaudePaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Claude;
    public static string    Label     => "Claude Code";
    public static string    CliBinary => "claude";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public ClaudePaths Paths { get; }

    // The desktop app runs a Claude Code it downloads under its own data dir, never on PATH.
    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        AppSignal      = apps => apps.Finds("Claude.app", Path.Combine("Claude", "claude-code"), "AnthropicClaude"),
        BundledCli     = ClaudeDesktopCli.Find,
        Wired          = () => ClaudePluginInstaller.IsPluginEnabled(Paths.UserSettings),
    };
}
