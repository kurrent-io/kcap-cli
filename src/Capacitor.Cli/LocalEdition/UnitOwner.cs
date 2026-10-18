namespace Capacitor.Cli.LocalEdition;

/// <summary>The unit is per user while installations are per root, so the loaded job and the file on disk
/// each name the root they serve, and either can be missing or disagree.</summary>
sealed record UnitOwner(OwnerReading Loaded, OwnerReading OnDisk);
