namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>Kiro as this process sees it.</summary>
public sealed class KiroHarness : IHarness<KiroHarness> {
    KiroHarness(KiroPaths paths) => Paths = paths;

    /// <summary>Resolves Kiro's one override, <c>KIRO_HOME</c>.</summary>
    public static KiroHarness FromEnvironment(UserHome home) => Over(new(home, Environment.GetEnvironmentVariable("KIRO_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static KiroHarness Over(KiroPaths paths) => new(paths);

    public static HarnessId Id    => HarnessId.Kiro;
    public static string    Label => "Kiro";

    public static string CliBinary => "kiro-cli";

    /// <summary>Kiro's IDE launcher. Not spawnable as an agent, but its presence means Kiro is here.</summary>
    const string IdeBinary = "kiro";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public KiroPaths Paths { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary) || probe.Finds(IdeBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => KiroHooksInstaller.IsInstalled(Paths.KcapAgentJson),
    };
}
