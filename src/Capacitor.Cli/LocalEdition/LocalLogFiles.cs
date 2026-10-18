using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The local stack's logs. The stores write through the server's logger, so the server's rolling log holds
/// theirs too; <see cref="LocalServerUnit.OutputLog"/> holds what came before the logger started.</summary>
static class LocalLogFiles {
    public static string Directory(LocalRoot root) => Path.Combine(root.Directory, "logs");

    /// <summary>The newest rolling log, then the output log when it has anything; the files that exist, in that order.</summary>
    public static IReadOnlyList<string> Current(LocalRoot root) {
        var dir = Directory(root);
        if (!System.IO.Directory.Exists(dir)) return [];

        var files = new List<string>(2);

        var newest = new DirectoryInfo(dir).EnumerateFiles("server-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (newest is not null) files.Add(newest.FullName);

        // The output log's path does not depend on the port.
        var output = LocalServerUnit.ForInstallation(root, 0).OutputLog;
        if (new FileInfo(output) is { Exists: true, Length: > 0 }) files.Add(output);

        return files;
    }
}
