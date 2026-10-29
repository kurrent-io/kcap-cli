namespace Capacitor.Cli.Core.Harness.MistralVibe;

/// <summary>Mistral Vibe as this process sees it.</summary>
public sealed class MistralVibeHarness : IHarness<MistralVibeHarness> {
    MistralVibeHarness(MistralVibePaths paths) => Paths = paths;

    /// <summary>Resolves Vibe's one override, <c>VIBE_HOME</c>, which names the config directory
    /// itself (it replaces <c>~/.vibe</c> wholesale).</summary>
    public static MistralVibeHarness FromEnvironment(UserHome home) =>
        Over(new(home, Environment.GetEnvironmentVariable("VIBE_HOME")));

    /// <summary>Over a layout resolved elsewhere — a test's, or a subset.</summary>
    public static MistralVibeHarness Over(MistralVibePaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.MistralVibe;
    public static string    Label     => "Mistral Vibe";
    public static string    CliBinary => "vibe";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built.</summary>
    public MistralVibePaths Paths { get; }

    // The PATH probe covers a fresh install that has written no markers yet.
    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => MistralVibeHooksInstaller.IsInstalled(Paths.HooksToml),
    };
}
