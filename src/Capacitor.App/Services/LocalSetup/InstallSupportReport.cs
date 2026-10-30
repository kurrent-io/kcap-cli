using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// The support email for a local install that never started: what the installer said on each attempt,
/// then the end of the local server's logs. A mail link carries no attachment, so the logs go in the body.
public static class InstallSupportReport {
    const int LogLines = 200;

    public static string Build(IReadOnlyList<string> failures, LocalRoot root) {
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture, $"kcap didn't finish starting during setup, after {failures.Count} attempts.\n\n");
        report.Append(CultureInfo.InvariantCulture, $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}\n\n");
        report.Append("What the installer said:\n");
        for (var i = 0; i < failures.Count; i++)
            report.Append(CultureInfo.InvariantCulture, $"{i + 1}. {failures[i]}\n");

        foreach (var log in Logs(root)) {
            report.Append(CultureInfo.InvariantCulture, $"\n--- {Path.GetFileName(log)} (last {LogLines} lines) ---\n");
            try {
                report.AppendJoin('\n', Tail(log, LogLines)).Append('\n');
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                report.Append(CultureInfo.InvariantCulture, $"(could not read: {e.Message})\n");
            }
        }

        return report.ToString();
    }

    /// The newest rolling server log, then the output log written before the server's logger started.
    static IEnumerable<string> Logs(LocalRoot root) {
        var dir = new DirectoryInfo(Path.Combine(root.Directory, "logs"));
        if (!dir.Exists) yield break;

        var newest = dir.EnumerateFiles("server-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (newest is not null) yield return newest.FullName;

        var output = new FileInfo(Path.Combine(dir.FullName, "server.out.log"));
        if (output is { Exists: true, Length: > 0 }) yield return output.FullName;
    }

    // The server may still hold its logs open, so they are read sharing writes and deletes.
    static IEnumerable<string> Tail(string path, int count) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>(count);
        while (reader.ReadLine() is { } line) {
            if (lines.Count == count) lines.Dequeue();
            lines.Enqueue(line);
        }

        return lines;
    }
}
