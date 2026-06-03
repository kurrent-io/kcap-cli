using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Per-session JSONL spool for canonical-event hooks whose POST failed.
/// File layout: <c>{spoolDir}/&lt;dashless-sid&gt;.jsonl</c>, one
/// <c>{"hook_event_name": "...", "body": &lt;raw payload string&gt;}</c>
/// object per line, in arrival order.
/// </summary>
public sealed partial class CursorHookSpool(string spoolDir, int capBytes = CursorHookSpool.DefaultCapBytes) {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    static readonly Regex SafeSessionId = SafeSessionIdRegex();

    string? PathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId)
            ? Path.Combine(spoolDir, $"{sessionId}.jsonl")
            : null;

    public void Append(string sessionId, string eventName, string rawPayloadJson) {
        var path = PathFor(sessionId);

        if (path is null) return;

        try {
            Directory.CreateDirectory(spoolDir);

            var line = new JsonObject {
                ["hook_event_name"] = eventName,
                ["body"]            = rawPayloadJson
            }.ToJsonString();
            EnsureUnderCap(path, line.Length + 1);
            File.AppendAllText(path, $"{line}\n");
        } catch {
            /* best effort */
        }
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;

            var size = new FileInfo(path).Length;

            if (size + incomingBytes <= capBytes) return;

            var lines = File.ReadAllLines(path).ToList();

            while (lines.Count > 0 && lines.Sum(l => l.Length + 1) + incomingBytes > capBytes) {
                lines.RemoveAt(0);
            }

            File.WriteAllLines(path, lines);
        } catch { }
    }

    public readonly struct Entry {
        public   string     EventName { get; init; }
        public   string     Body      { get; init; }
        internal int        Index     { get; init; }
        internal Func<Task> Deliver   { get; init; }

        public Task MarkDeliveredAsync() => Deliver();
    }

    /// <summary>
    /// FIFO drain. Yields one entry per call. Caller MUST invoke
    /// <see cref="Entry.MarkDeliveredAsync"/> after a successful POST.
    /// Stop iterating to leave the rest of the queue for next time.
    /// </summary>
    public async IAsyncEnumerable<Entry> DrainAsync(string sessionId, [EnumeratorCancellation] CancellationToken ct) {
        var path = PathFor(sessionId);

        if (path is null || !File.Exists(path)) yield break;

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); } catch { yield break; }

        var delivered = 0;

        for (var i = 0; i < lines.Length; i++) {
            ct.ThrowIfCancellationRequested();

            var     line = lines[i];
            string? eventName;
            string? body;

            try {
                var node = JsonNode.Parse(line);
                eventName = node?["hook_event_name"]?.GetValue<string>();
                body      = node?["body"]?.GetValue<string>();
            } catch {
                eventName = null;
                body      = null;
            }

            if (eventName is null || body is null) {
                // Malformed line — could be a partially-written tail from a racing
                // Append. Stop draining; leave the file as-is so the next invocation
                // can re-read once the writer finishes.
                yield break;
            }

            var capturedDelivered = delivered;

            yield return new Entry {
                EventName = eventName,
                Body      = body,
                Index     = i,
                Deliver = () => {
                    delivered = capturedDelivered + 1;

                    return WriteRemainingAsync(path, lines, delivered);
                }
            };
        }

        if (delivered == lines.Length) {
            try { File.Delete(path); } catch { }
        }
    }

    static async Task WriteRemainingAsync(string path, string[] lines, int delivered) {
        try {
            if (delivered >= lines.Length) {
                File.Delete(path);

                return;
            }

            await File.WriteAllLinesAsync(path, lines.Skip(delivered));
        } catch { }
    }

    public void DeleteSession(string sessionId) {
        var path = PathFor(sessionId);

        if (path is null) return;

        try {
            if (File.Exists(path)) File.Delete(path);
        } catch { }
    }

    public void ReapOlderThan(TimeSpan age) {
        try {
            if (!Directory.Exists(spoolDir)) return;

            var cutoff = DateTime.UtcNow - age;

            foreach (var file in Directory.EnumerateFiles(spoolDir, "*.jsonl")) {
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                } catch { }
            }
        } catch { }
    }

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
