using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// The support email for a local install that never started: what the installer said on each attempt,
/// then the end of the local server's logs. A mail link carries no attachment, so the logs go in the body.
public static class InstallSupportReport {
    const int LogLines      = 200;
    const int TailBytes     = 64 * 1024;
    const int MaxLineLength = 500;

    public static string Build(IReadOnlyList<string> failures, LocalRoot root) {
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture, $"kcap didn't finish starting during setup, after {failures.Count} attempts.\n\n");
        report.Append(CultureInfo.InvariantCulture, $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}\n\n");
        report.Append("What the installer said:\n");
        for (var i = 0; i < failures.Count; i++)
            report.Append(CultureInfo.InvariantCulture, $"{i + 1}. {failures[i]}\n");

        IReadOnlyList<string> logs;
        try {
            logs = Logs(root);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            report.Append(CultureInfo.InvariantCulture, $"\n(could not list the server logs: {e.Message})\n");
            return report.ToString();
        }

        foreach (var log in logs) {
            report.Append(CultureInfo.InvariantCulture, $"\n--- {Path.GetFileName(log)} (last {LogLines} lines) ---\n");
            try {
                report.AppendJoin('\n', Tail(log)).Append('\n');
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                report.Append(CultureInfo.InvariantCulture, $"(could not read: {e.Message})\n");
            }
        }

        return report.ToString();
    }

    /// The newest rolling server log, then the output log written before the server's logger started.
    static List<string> Logs(LocalRoot root) {
        var dir = new DirectoryInfo(Path.Combine(root.Directory, "logs"));
        if (!dir.Exists) return [];

        var found  = new List<string>(2);
        var newest = dir.EnumerateFiles("server-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is not null) found.Add(newest.FullName);

        var output = new FileInfo(Path.Combine(dir.FullName, "server.out.log"));
        if (output is { Exists: true, Length: > 0 }) found.Add(output.FullName);

        return found;
    }

    // Only the end of the file is read, so a large log neither stalls the window nor swells the report.
    // The server may still hold its logs open, so they are read sharing writes and deletes.
    static IEnumerable<string> Tail(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        // A read that starts mid-file starts mid-line.
        if (start > 0) reader.ReadLine();

        var lines = new Queue<string>(LogLines);
        while (reader.ReadLine() is { } line) {
            if (lines.Count == LogLines) lines.Dequeue();
            lines.Enqueue(line.Length > MaxLineLength ? line[..MaxLineLength] + "…" : line);
        }

        return lines;
    }
}
