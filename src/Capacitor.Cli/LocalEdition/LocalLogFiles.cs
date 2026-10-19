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

    /// <summary>The last <paramref name="count"/> lines, read back from the end: the output log is never rotated. Opened
    /// sharing writes and deletes, since the server holds its logs open.</summary>
    public static IReadOnlyList<string> Tail(string path, int count) {
        const int block = 64 * 1024;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var position = stream.Length;
        var chunks   = new List<byte[]>();
        var newlines = 0;

        // One newline more than the count is needed to know the first line is whole; a trailing one ends the last line.
        while (position > 0 && newlines <= count) {
            var size = (int)Math.Min(block, position);
            position -= size;

            var chunk = new byte[size];
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(chunk);

            newlines += chunk.Count(b => b == (byte)'\n');
            chunks.Add(chunk);
        }

        chunks.Reverse();

        var text  = System.Text.Encoding.UTF8.GetString([.. chunks.SelectMany(c => c)]);
        var lines = text.Split('\n');
        var whole = lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;

        return [.. whole.Skip(Math.Max(0, whole.Length - count)).Select(l => l.TrimEnd('\r'))];
    }
}
