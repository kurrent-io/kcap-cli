using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// Three independent things a harness can say about itself on this machine. A vendor leaves one
/// null when that question has no answer for it rather than a false one: Cursor has no launch
/// signal, because it ships as an editor and is known by the data it writes, not by a name on PATH.
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

    /// <summary>Whether kcap's hook or extension is registered with the vendor.</summary>
    public Func<bool>? Wired { get; init; }

    /// <summary>Asks the launch question now. A vendor that cannot answer reads as not launchable.</summary>
    public bool CanLaunch(BinaryProbe binaries) => LaunchSignal?.Invoke(binaries) ?? false;

    /// <summary>Asks the user-data question now. A vendor that cannot answer reads as absent.</summary>
    public bool HasUserData => UserDataSignal?.Invoke() ?? false;

    /// <summary>Asks the wiring question now. A vendor that cannot answer reads as not wired.</summary>
    public bool IsWired => Wired?.Invoke() ?? false;
}
