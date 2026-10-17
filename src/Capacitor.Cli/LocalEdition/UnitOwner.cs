namespace Capacitor.Cli.LocalEdition;

/// <summary>What one source says about which installation root a unit belongs to.</summary>
abstract record OwnerReading {
    OwnerReading() { }

    public sealed record Absent : OwnerReading {
        public static Absent Instance { get; } = new();
    }

    public sealed record Root(string Path) : OwnerReading;

    public sealed record Unreadable(string Reason) : OwnerReading;
}

/// <summary>The unit is per user while installations are per root, so the loaded job and the file on disk
/// each name the root they serve, and either can be missing or disagree.</summary>
sealed record UnitOwner(OwnerReading Loaded, OwnerReading OnDisk);
