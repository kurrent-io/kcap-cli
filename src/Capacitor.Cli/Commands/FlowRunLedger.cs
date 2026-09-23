using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The flows this machine started, by workspace, for a harness that gives the flows server no
/// session identity: a status call without a <c>flow_run_id</c> reads its candidates here instead of
/// asking the server by session. Best-effort on both sides — a lost write costs only that lookup,
/// and a missing or corrupt file reads as empty.
/// </summary>
public sealed class FlowRunLedger(ConfigRoot config, TimeProvider time) {
    public const string FileName = "flow-runs-v1.json";
    public const int MaxEntries = 50;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    readonly string _path = config.Path(FileName);

    /// <summary>False when the run could not be persisted; never throws.</summary>
    public bool Record(string flowRunId, string workspace) {
        IDisposable lease;
        try { lease = config.AcquireLock(FileName, TimeSpan.FromSeconds(2)); } catch { return false; }

        using (lease) {
            try {
                var now     = time.GetUtcNow();
                var entries = Load(now).Where(e => e.FlowRunId != flowRunId).ToList();
                entries.Insert(0, new(flowRunId, workspace, now));

                var rows = new JsonArray();
                foreach (var e in entries.Take(MaxEntries))
                    rows.Add((JsonNode)new JsonObject {
                        ["flow_run_id"] = e.FlowRunId,
                        ["workspace"]   = e.Workspace,
                        ["started_at"]  = e.StartedAt.ToString("O"),
                    });

                AtomicFile.Replace(_path, new JsonObject { ["runs"] = rows }.ToJsonString());
                return true;
            } catch {
                return false;
            }
        }
    }

    /// <summary>This workspace's runs, newest first.</summary>
    public IReadOnlyList<string> Recent(string workspace, int limit) =>
        Load(time.GetUtcNow())
            .Where(e => e.Workspace == workspace)
            .Take(limit)
            .Select(e => e.FlowRunId)
            .ToList();

    List<Entry> Load(DateTimeOffset now) {
        try {
            if (!File.Exists(_path) || JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject { } root
                                    || root["runs"] is not JsonArray rows)
                return [];

            var entries = new List<Entry>();
            foreach (var row in rows.OfType<JsonObject>()) {
                if (Text(row, "flow_run_id") is not { } flowRunId || Text(row, "workspace") is not { } workspace
                    || !DateTimeOffset.TryParse(Text(row, "started_at"), out var startedAt)
                    || now - startedAt > Retention)
                    continue;
                entries.Add(new(flowRunId, workspace, startedAt));
            }

            return entries;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return [];
        }
    }

    static string? Text(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    readonly record struct Entry(string FlowRunId, string Workspace, DateTimeOffset StartedAt);
}
