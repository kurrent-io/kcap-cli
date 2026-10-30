using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// <summary>
/// The app version whose post-update component work, the local server's upgrade, last settled. A launch of any
/// other version owes that work whatever route installed it: in-app, a pending package applied at startup, the
/// disk image, or a copy dragged over the old one in Finder. Missing counts as owed, so an app that predates
/// the stamp still upgrades its local server on its first launch.
/// </summary>
public static class AppComponentStamp {
    public const string FileName = "app-components-version";

    public static bool Owed(ConfigRoot config, string version) => Read(config) != version;

    /// A failed upgrade is retried by the next launch, and so is one whose local server build was not published
    /// yet: the server can be published after the app, and nothing else would upgrade it until the next app update.
    public static bool SettledBy(LocalUpgradeOutcome outcome) =>
        outcome is not (LocalUpgradeOutcome.Failed or LocalUpgradeOutcome.NotPublished);

    public static string? Read(ConfigRoot config) {
        try {
            var path = config.Path(FileName);
            return File.Exists(path) && new FileInfo(path).LinkTarget is null ? File.ReadAllText(path).Trim() : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public static void Write(ConfigRoot config, string version) {
        Directory.CreateDirectory(config.Directory);
        var path = config.Path(FileName);
        var staging = path + "." + Guid.NewGuid().ToString("N");
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(version);
            File.Move(staging, path, true);
        } finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
