namespace Capacitor.Cli.Core.Harness.Muse;

public sealed class MuseHarness : IHarness<MuseHarness> {
    MuseHarness(MusePaths paths) => Paths = paths;

    public static MuseHarness FromEnvironment(UserHome home) =>
        Over(new(home, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetEnvironmentVariable("XDG_DATA_HOME")));

    public static MuseHarness Over(MusePaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Muse;
    public static string    Label     => "Muse Code";
    public static string    CliBinary => "muse";

    public MusePaths Paths { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => false,
    };
}
