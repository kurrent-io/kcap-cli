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
