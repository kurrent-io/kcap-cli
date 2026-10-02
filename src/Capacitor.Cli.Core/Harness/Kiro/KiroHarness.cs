namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>Kiro as this process sees it.</summary>
public sealed class KiroHarness : IHarness<KiroHarness> {
    KiroHarness(KiroPaths paths, KiroCrewPaths crew) {
        Paths = paths;
        Crew  = crew;
    }

    /// <summary>Resolves Kiro's override, <c>KIRO_HOME</c>, and Kiro Crew's, <c>KIROCREW_HOME</c>. Crew
    /// resolves its own tree from the user's home, not from <c>KIRO_HOME</c>.</summary>
    public static KiroHarness FromEnvironment(UserHome home) => new(
        new(home, Environment.GetEnvironmentVariable("KIRO_HOME")),
        new(Path.Combine(home.Path, ".kiro"), Environment.GetEnvironmentVariable("KIROCREW_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static KiroHarness Over(KiroPaths paths) => new(paths, new(paths.ConfigRoot, null));

    public static HarnessId Id    => HarnessId.Kiro;
    public static string    Label => "Kiro";

    public static string CliBinary => "kiro-cli";

    /// <summary>Kiro's IDE launcher. Not spawnable as an agent, but its presence means Kiro is here.</summary>
    const string IdeBinary = "kiro";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public KiroPaths Paths { get; }

    /// <summary>Kiro Crew's layout. Crew runs this same <c>kiro-cli</c>, so it is part of Kiro.</summary>
    public KiroCrewPaths Crew { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary) || probe.Finds(IdeBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => KiroHooksInstaller.IsInstalled(Paths.KcapAgentJson),
    };
}
