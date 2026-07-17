using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4(2a)): the in-memory kill-quarantine — agents whose death could NOT be
/// confirmed at teardown (kill failed / record write failed while the process is still alive). The
/// heartbeat retries the kill until it confirms death. While an entry sits here it:
/// <list type="bullet">
/// <item>counts against the daemon's admission gate (<c>EffectiveCount = ActiveCount + Quarantined.Count</c>),
/// so a persistent kill-failure mode FAILS CLOSED — it can never mint unbounded processes;</item>
/// <item>is reported in <c>DaemonStatusReport.Quarantined</c> (its own field, excluded from
/// <c>ActiveCount</c>) so the server can see WHY admission is blocked and (Phase B2) treat the id as
/// physically-live for heal/settlement.</item>
/// </list>
/// </summary>
internal sealed class AgentKillQuarantine(ILogger logger) {
    /// <summary>A quarantined process — enough to retry the kill by exact identity and to report it.</summary>
    internal readonly record struct Entry(
        string AgentId, int Pid, string Identity, string Kind, DateTimeOffset CreatedAt,
        string? FlowRunId, string? FlowRole);

    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    /// <summary>Quarantine an agent whose death is unconfirmed (idempotent per agent id).</summary>
    public void Add(Entry entry) => _entries[entry.AgentId] = entry;

    /// <summary>Snapshot for <c>DaemonStatusReport.Quarantined</c>.</summary>
    public IReadOnlyList<QuarantinedAgentInfo> Snapshot() =>
        [.. _entries.Values.Select(e => new QuarantinedAgentInfo(e.AgentId, e.Kind, e.CreatedAt, e.FlowRunId, e.FlowRole))];

    /// <summary>Heartbeat retry: attempt to reap every quarantined process by exact identity and drain
    /// those confirmed gone. Best-effort per entry — a still-alive one is retained for the next tick.
    /// Returns the agent ids DRAINED this pass (death/recycle confirmed) so the caller deletes their
    /// durable PID records — a quarantined survivor's record is retained by teardown and, carrying the
    /// current epoch, would otherwise be skipped by the orphan sweep and leak until restart.</summary>
    public async Task<IReadOnlyList<string>> RetryAllAsync(CancellationToken ct) {
        var drained = new List<string>();

        foreach (var entry in _entries.Values) {
            try {
                if (await ProcessReaper.ReapByIdentityAsync(entry.Pid, entry.Identity, entry.AgentId, logger, ct)
                    && _entries.TryRemove(new KeyValuePair<string, Entry>(entry.AgentId, entry)))
                    drained.Add(entry.AgentId);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                logger.LogWarning(ex, "AgentKillQuarantine: retry failed for {AgentId} (pid {Pid})", entry.AgentId, entry.Pid);
            }
        }

        return drained;
    }
}
