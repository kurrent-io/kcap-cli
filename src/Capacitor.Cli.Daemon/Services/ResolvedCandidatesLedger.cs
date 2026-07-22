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
            var entry = new ResolvedStartupCandidate(_nextGeneration, agentId, oldEpoch, flowRunId, flowRole);
            _entries[key] = entry;
            // Phase B2-b (sequenced-settlement design §5.5): persist the PROSPECTIVE state (with the
            // post-increment high-water) BEFORE committing the counter in memory, and roll back the
            // in-memory entry if the durable write throws — so memory never leads disk. A leftover
            // in-memory-only entry would otherwise satisfy the next sweep's idempotent short-circuit above
            // WITHOUT persisting, and the caller would then delete the durable source (append-before-delete
            // is the invariant that keeps a crash re-derivable).
            try { PersistState(_nextGeneration + 1); }
            catch { _entries.Remove(key); throw; }
            _nextGeneration++; // commit the counter only after a durable write succeeded
            return entry;
        }
    }

    public IReadOnlyList<ResolvedStartupCandidate> Snapshot() {
        lock (_lock) return [.. _entries.Values.OrderBy(e => e.Generation)];
    }

    /// <summary>Phase B2-b (sequenced-settlement design §5.5): the daemon-lifetime monotonic high-water
    /// of minted generations — 0 before any mint, N after N distinct Upserts. Persists across prunes and
    /// restarts (<see cref="_nextGeneration"/> is never decremented and Load restores it), so once sparse
    /// acks prune entries the server still learns the generation frontier.</summary>
    public long HighestResolutionGeneration { get { lock (_lock) return _nextGeneration - 1; } }

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

    // Ack removes an entry then persists. A persist failure there leaves memory BEHIND disk, which is
    // benign: the entry re-loads + re-reports on the next restart and the server re-acks it idempotently.
    // So this direction needs NO rollback (only Upsert's memory-leads-disk direction does).
    void Persist() => PersistState(_nextGeneration);

    void PersistState(long nextGeneration) {
        var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            new Persisted(nextGeneration, [.. _entries.Values]), LedgerJsonCtx.Default.Persisted));
        File.Move(tmp, _path, overwrite: true); // atomic same-directory rename
    }
}
