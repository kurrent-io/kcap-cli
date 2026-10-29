namespace Capacitor.App.Services.LocalSetup;

/// What <see cref="StaleClientRepair"/> left behind; anything but Clear and Repaired keeps the install from starting.
public abstract record ClientRepairOutcome {
    ClientRepairOutcome() { }

    /// Nothing on this Mac was below the floor.
    public sealed record Clear : ClientRepairOutcome;

    public sealed record Repaired(string Path) : ClientRepairOutcome;

    /// The administrator prompt was dismissed.
    public sealed record Cancelled(string Path) : ClientRepairOutcome;

    public sealed record Failed(string Path, string Reason) : ClientRepairOutcome;

    /// Another copy of the desktop app is below the floor; only the user can remove an app bundle.
    public sealed record OldApp(string Path) : ClientRepairOutcome;

    public bool Ready => this is Clear or Repaired;
}
