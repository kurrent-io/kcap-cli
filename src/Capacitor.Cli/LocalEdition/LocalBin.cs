using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary><c>bin/</c> under the root: one directory per staged version, and <c>bin/current</c>, the link the unit
/// runs the server through.</summary>
static class LocalBin {
    public static string Directory(LocalRoot root) => Path.Combine(root.Directory, "bin");

    public static string StagingDir(LocalRoot root, string name) => Path.Combine(Directory(root), $".staging-{name}");

    /// <summary>Points <c>bin/current</c> at <paramref name="version"/> by an atomic rename.</summary>
    public static void Switch(LocalRoot root, string version) {
        Commands.ScriptUpdater.SwitchLink(Path.Combine(Directory(root), "current"), version);
        DurableDirectory.Sync(Directory(root));
    }

    /// <summary>The version <c>bin/current</c> names, or null when it is missing or not a link.</summary>
    public static string? Current(LocalRoot root) =>
        new FileInfo(Path.Combine(Directory(root), "current")).LinkTarget is { } target
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(target))
            : null;

    public static bool Has(LocalRoot root, string version) => System.IO.Directory.Exists(Path.Combine(Directory(root), version));

    /// <summary>Removes a staging directory and a version directory, never the one <c>bin/current</c> names.</summary>
    public static void Discard(LocalRoot root, string stagingName, string version) {
        var staging = StagingDir(root, stagingName);
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);

        var dir = Path.Combine(Directory(root), version);
        if (Current(root) != version && System.IO.Directory.Exists(dir)) {
            System.IO.Directory.Delete(dir, recursive: true);
            FaultPoints.Hit($"unlink:{dir}");
        }

        if (System.IO.Directory.Exists(Directory(root))) DurableDirectory.Sync(Directory(root));
    }
}
