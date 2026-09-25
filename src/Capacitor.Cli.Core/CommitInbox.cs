using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core;

/// <summary>
/// The commits made in one agent session, waiting to be sent to Capacitor. kcap's git hook adds a
/// line for each commit the moment git makes it, and the session's watcher picks new lines up and
/// sends them. The commit never waits on the network, and a commit made while the watcher is down
/// is still here when it comes back.
/// </summary>
public sealed class CommitInbox(string path) {
    const FileShare Shared = FileShare.ReadWrite | FileShare.Delete;

    public static CommitInbox Of(ConfigRoot config, SessionId session) => new(config.Path("commits", $"{session}.jsonl"));

    /// <summary>
    /// Not deleted when the session ends: a resumed session's watcher reads its inbox again.
    /// </summary>
    public static void ReapOlderThan(ConfigRoot config, TimeSpan age, TimeProvider time) {
        try {
            var dir = config.Path("commits");
            if (!Directory.Exists(dir)) return;

            var cutoff = (time.GetUtcNow() - age).UtcDateTime;

            foreach (var file in Directory.EnumerateFiles(dir)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }

    /// <summary>
    /// One unbuffered, shared write, so a concurrent hook or the reading watcher never meets a lock.
    /// Led by a newline too, so a record a failed write cut short never swallows the next one.
    /// </summary>
    public void Append(ObservedCommit commit) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, Shared, bufferSize: 1);
        stream.Write(Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(commit, CapacitorJsonContext.Default.ObservedCommit) + "\n"));
    }

    /// <summary>
    /// A line the hook is still writing waits for the next read.
    /// </summary>
    public (IReadOnlyList<ObservedCommit> Commits, long Next) ReadFrom(long offset) {
        if (!File.Exists(path)) return ([], offset);

        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, Shared);
            var start = stream.Length < offset ? 0 : offset;
            var bytes = new byte[stream.Length - start];

            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(bytes);

            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) return ([], start);

            var commits = Encoding.UTF8.GetString(bytes, 0, end)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(Parse)
                .OfType<ObservedCommit>()
                .ToList();

            return (commits, start + end + 1);
        } catch (IOException) {
            return ([], offset);
        }
    }

    static ObservedCommit? Parse(string line) {
        try {
            return JsonSerializer.Deserialize(line, CapacitorJsonContext.Default.ObservedCommit);
        } catch (JsonException) {
            return null;
        }
    }
}
