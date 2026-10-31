using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// Independent things a harness can say about itself on this machine; any one of the detection
/// signals is enough. A vendor leaves one null when that question has no answer for it rather than
/// a false one: Cursor has no launch signal, because it ships as an editor, not a name on PATH.
///
/// <para>No detection signal may name something kcap or a generic installer creates — a bare vendor
/// directory, <c>skills/</c>, kcap's own hook, plugin or MCP entries — or wiring a vendor would make
/// it detect itself after it is uninstalled.</para>
///
/// <para>Predicates rather than answers because the callers differ in what they may spend: the
/// SessionStart nudge runs on a latency budget and asks one, while <c>kcap status</c> asks all.</para>
/// </summary>
public readonly record struct HarnessSignals {
    public HarnessSignals() { }

    /// <summary>Whether this vendor's CLI can be launched, over the search path it is handed.</summary>
    public Func<BinaryProbe, bool>? LaunchSignal { get; init; }

    /// <summary>Whether this vendor's own user-level data exists here.</summary>
    public Func<bool>? UserDataSignal { get; init; }

    /// <summary>Whether this vendor's desktop app, or the data it keeps, is on this machine.</summary>
    public Func<DesktopAppProbe, bool>? AppSignal { get; init; }

    /// <summary>The CLI this vendor's desktop app bundles off PATH, or null. Not a detection signal:
    /// it answers what to launch once a vendor is found.</summary>
    public Func<DesktopAppProbe, string?>? BundledCli { get; init; }

    /// <summary>Whether kcap's hook or extension is registered with the vendor.</summary>
    public Func<bool>? Wired { get; init; }

    /// <summary>Asks the launch question now. A vendor that cannot answer reads as not launchable.</summary>
    public bool CanLaunch(BinaryProbe binaries) => LaunchSignal?.Invoke(binaries) ?? false;

    /// <summary>Asks the user-data question now. A vendor that cannot answer reads as absent.</summary>
    public bool HasUserData => UserDataSignal?.Invoke() ?? false;

    /// <summary>Asks the desktop-app question now. A vendor with no app reads as absent.</summary>
    public bool HasDesktopApp(DesktopAppProbe apps) => AppSignal?.Invoke(apps) ?? false;

    /// <summary>Looks for the bundled CLI now. A vendor whose app bundles none reads as null.</summary>
    public string? FindBundledCli(DesktopAppProbe apps) => BundledCli?.Invoke(apps);

    /// <summary>Asks the wiring question now. A vendor that cannot answer reads as not wired.</summary>
    public bool IsWired => Wired?.Invoke() ?? false;
}
