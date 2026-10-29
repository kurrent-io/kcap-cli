namespace Capacitor.Cli.Core.Setup;

/// Puts a resolved CLI on the terminal PATH, one mechanism per OS.
public interface ICliPathInstaller {
    /// What installing does, shown before the user agrees to it.
    string Disclosure { get; }

    /// What installing would do on this machine, with paths marked as code. Changes nothing.
    Task<IReadOnlyList<DisclosureSpan>> DescribeAsync(string target, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DisclosureSpan>>([new(Disclosure)]);

    ShimPreflight Preflight(string target);

    Task<ShimResult> InstallAsync(string target, CancellationToken ct);
}
