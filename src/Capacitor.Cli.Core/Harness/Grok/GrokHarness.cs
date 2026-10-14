namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>Grok Build as this process sees it.</summary>
public sealed class GrokHarness : IHarness<GrokHarness> {
    GrokHarness(GrokPaths paths) => Paths = paths;

    /// <summary>Resolves Grok's one override, <c>GROK_HOME</c>, which replaces <c>~/.grok</c>.</summary>
    public static GrokHarness FromEnvironment(UserHome home) =>
        Over(new(home, Environment.GetEnvironmentVariable("GROK_HOME")));

    public static GrokHarness Over(GrokPaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Grok;
    public static string    Label     => "Grok";
    public static string    CliBinary => "grok";

    public GrokPaths Paths { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => GrokHooksInstaller.IsInstalled(Paths.KcapHooksJson),
    };
}
