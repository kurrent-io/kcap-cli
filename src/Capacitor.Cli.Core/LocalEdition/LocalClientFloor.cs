namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The client floor of a local installation, for the CLI, the daemon and the desktop app alike: a kcap must be newer
/// than <see cref="NewestRefused"/>. An older copy does not know the local rules, so it would post past the
/// destination check and spool where nothing drains. The local server refuses by the same number, so the two must
/// move together.
/// </summary>
public static class LocalClientFloor {
    /// <summary>The newest release that does not keep to the local rules. Every build after it, prereleases
    /// included, is admitted.</summary>
    public const string NewestRefused = "1.3.0";

    /// <summary>A version that cannot be read is not admitted.</summary>
    public static bool Admits(string? version) => PrereleaseSemver.Compare(version, NewestRefused) > 0;
}
