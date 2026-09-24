namespace Capacitor.Cli.Core.Setup;

/// Puts a resolved CLI on the terminal PATH, one mechanism per OS.
public interface ICliPathInstaller {
    /// What installing does, shown before the user agrees to it.
    string Disclosure { get; }

    ShimPreflight Preflight(string target);

    Task<ShimResult> InstallAsync(string target, CancellationToken ct);
}
