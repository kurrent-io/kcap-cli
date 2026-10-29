namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Where this host keeps desktop apps and their per-user data, so a vendor installed only as an app —
/// with its CLI bundled inside it, off PATH — is still found. A vendor names entries relative to these
/// roots (<c>Claude.app</c>, <c>Codex</c>); any entry under any root is a hit.
/// </summary>
public sealed class DesktopAppProbe {
    readonly IReadOnlyList<string> _roots;

    DesktopAppProbe(IReadOnlyList<string> roots) => _roots = roots;

    /// <summary>Finds nothing: what a registry built over a test's layout looks through.</summary>
    public static DesktopAppProbe None { get; } = new([]);

    public static DesktopAppProbe Searching(params string[] roots) => new(roots);

    /// <summary>
    /// Linux has no root: its Electron data shares <c>~/.config</c> with the CLI config kcap itself
    /// writes into, so a hit there would not mean an app was installed.
    /// </summary>
    public static DesktopAppProbe FromEnvironment(UserHome home) =>
        OperatingSystem.IsMacOS() ? new([
            "/Applications",
            Path.Combine(home.Path, "Applications"),
            Path.Combine(home.Path, "Library", "Application Support"),
        ])
        : OperatingSystem.IsWindows() ? new([
            Path.Combine(home.Path, "AppData", "Local"),
            Path.Combine(home.Path, "AppData", "Local", "Programs"),
            Path.Combine(home.Path, "AppData", "Roaming"),
        ])
        : None;

    /// <summary>Whether any of <paramref name="entries"/> exists, as a file or a directory, under any root.</summary>
    public bool Finds(params string[] entries) =>
        _roots.Any(root => entries.Any(entry => {
            var path = Path.Combine(root, entry);

            return Directory.Exists(path) || File.Exists(path);
        }));
}
