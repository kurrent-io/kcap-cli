namespace Capacitor.Cli.Core.Setup;

/// The folders a `kcap` command link is installed into, most system-wide first.
public static class KcapLinkFolders {
    public static IReadOnlyList<string> For(UserHome home) => [
        "/usr/local/bin",
        "/opt/homebrew/bin",
        Path.Combine(home.Path, ".local", "bin"),
        Path.Combine(home.Path, "bin")
    ];
}
