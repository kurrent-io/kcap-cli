using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>This root's installation as a <c>kcap local</c> command finds it.</summary>
abstract record LocalStackState {
    LocalStackState() { }

    public sealed record NotInstalled : LocalStackState {
        public static NotInstalled Instance { get; } = new();
    }

    /// <summary>The registry or the journal exists but cannot be read; nothing may act on the installation.</summary>
    public sealed record Unreadable(string Path, string Reason) : LocalStackState;

    /// <summary>A lifecycle command stopped part way. Only a read without the lease can see this: under it the
    /// journal is recovered first.</summary>
    public sealed record Interrupted(string Operation, string Phase) : LocalStackState;

    /// <summary>The registry's active installation. The unit is per user, so <paramref name="Readings"/> may name
    /// another root's job; act on the unit only when <paramref name="Owner"/> is <see cref="OwnerDecision.Ours"/>.</summary>
    public sealed record Installed(LocalInstallation Installation, LocalServerUnit Unit, UnitOwner Readings, OwnerDecision Owner)
        : LocalStackState {
        public bool UnitAbsent => Readings is { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent };
    }
}
