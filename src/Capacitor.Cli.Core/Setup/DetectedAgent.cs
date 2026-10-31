using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// One vendor's independent detection signals: a PATH binary probe, the files the vendor writes
/// (<see cref="HarnessSignals.UserDataSignal"/>) and its desktop app (<see cref="HarnessSignals.AppSignal"/>).
/// <see cref="Detected"/> is their OR: a fresh install has written nothing yet, and a desktop app
/// bundles its CLI off PATH.
/// </summary>
public sealed record DetectedAgent(bool BinaryFound, bool InstallSignalFound, bool DesktopAppFound = false) {
    /// <summary>No signal — what an id outside this process's registry reads as.</summary>
    public static readonly DetectedAgent None = new(false, false);

    public bool Detected => BinaryFound || InstallSignalFound || DesktopAppFound;
}
