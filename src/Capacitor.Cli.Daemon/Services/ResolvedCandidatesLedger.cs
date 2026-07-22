using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.4): the durable outbox of positive per-id death
/// evidence. Persisted atomically (temp+rename) in the daemon state dir; the monotonic
/// <c>Generation</c> counter persists WITH it (spans epochs + restarts). Upsert is idempotent on the
/// source-stable <c>(AgentId, OldEpoch)</c> key — the same key never mints a second entry, so a
/// crash-reconciled re-append (source leftover matched by <c>(AgentId, OldEpoch)</c>) collapses onto
/// the committed entry. <c>Generation</c> is the server-facing ack/ordering id only. Entries are
/// re-reported every connect/report until <see cref="Ack"/> prunes them sparsely.
/// </summary>
internal sealed partial class ResolvedCandidatesLedger {
    readonly string _path;
    readonly ILogger _logger;
    readonly object _lock = new();
    readonly Dictionary<(string AgentId, string OldEpoch), ResolvedStartupCandidate> _entries = new();
    long _nextGeneration = 1;

    public ResolvedCandidatesLedger(string stateDir, ILogger logger) {
        Directory.CreateDirectory(stateDir);
        _path = Path.Combine(stateDir, "resolved-candidates.json");
        _logger = logger;
        Load();
    }

    public ResolvedStartupCandidate Upsert(string agentId, string oldEpoch, string? flowRunId, string? flowRole) {
        lock (_lock) {
            var key = (agentId, oldEpoch);
            if (_entries.TryGetValue(key, out var existing)) return existing; // idempotent — no new generation
            var entry = new ResolvedStartupCandidate(_nextGeneration++, agentId, oldEpoch, flowRunId, flowRole);
            _entries[key] = entry;
            Persist();
            return entry;
        }
    }

    public IReadOnlyList<ResolvedStartupCandidate> Snapshot() {
        lock (_lock) return [.. _entries.Values.OrderBy(e => e.Generation)];
    }

    public void Ack(IEnumerable<ResolvedCandidateAck> entries) {
        lock (_lock) {
            var changed = false;
            foreach (var a in entries) {
                var key = (a.AgentId, a.OldEpoch);
                if (_entries.TryGetValue(key, out var e) && e.Generation == a.Generation)
                    changed |= _entries.Remove(key);
            }
            if (changed) Persist();
        }
    }

    // ── durable state (persist the entries AND the generation high-water together) ────────────────
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(Persisted))]
    partial class LedgerJsonCtx : JsonSerializerContext;

    readonly record struct Persisted(long NextGeneration, ResolvedStartupCandidate[] Entries);

    void Load() {
        try {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize(File.ReadAllText(_path), LedgerJsonCtx.Default.Persisted);
            _nextGeneration = Math.Max(1, p.NextGeneration);
            foreach (var e in p.Entries ?? []) _entries[(e.AgentId, e.OldEpoch)] = e;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "ResolvedCandidatesLedger: unreadable ledger — starting empty (re-derived from sources next boot)");
        }
    }

    void Persist() {
        var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new Persisted(_nextGeneration, [.. _entries.Values]), LedgerJsonCtx.Default.Persisted));
        File.Move(tmp, _path, overwrite: true); // atomic same-directory rename
    }
}
