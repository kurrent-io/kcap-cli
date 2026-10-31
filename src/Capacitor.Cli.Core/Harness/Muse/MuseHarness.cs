namespace Capacitor.Cli.Core.Harness.Muse;

public sealed class MuseHarness : IHarness<MuseHarness> {
    MuseHarness(MusePaths paths) => Paths = paths;

    public static MuseHarness FromEnvironment(UserHome home) => FromEnvironment(home, Environment.GetEnvironmentVariable);

    /// <summary>An unset XDG variable falls back to the root persisted at install, then to the default:
    /// a Muse hook never sees the variables the installing shell had.</summary>
    public static MuseHarness FromEnvironment(UserHome home, Func<string, string?> env) {
        var persisted = MuseHookRoots.Read(home);

        return Over(new(home,
            Set(env("XDG_CONFIG_HOME")) ?? persisted.ConfigHome,
            Set(env("XDG_DATA_HOME"))   ?? persisted.DataHome));
    }

    public static MuseHarness Over(MusePaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Muse;
    public static string    Label     => "Muse Code";
    public static string    CliBinary => "muse";

    public MusePaths Paths { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        // "Muse" alone names unrelated apps; the bundled agent UI does not.
        AppSignal      = apps => apps.Finds(Path.Combine("Muse.app", "Contents", "Resources", "hatch")),
        Wired          = () => MuseHooksInstaller.IsWired(Paths.SettingsJson),
    };

    static string? Set(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
