using System.Text.Json;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1313 Phase B (D4 §6.4(2)): durable per-agent PID records under
/// <c>&lt;state-dir&gt;/agents/{agentId}.json</c>. Written atomically at spawn (temp-file +
/// same-directory rename — no fsync needed; a lost record falls to the env-marker scan) so a restarted
/// daemon can reap a surviving child by exact identity. A record is deleted ONLY after confirmed death
/// (caller's rule). An unparseable file is quarantined (renamed <c>.corrupt</c>) and excluded — never
/// acted on.
/// </summary>
internal sealed class AgentPidRecordStore(string stateDir, ILogger logger) {
    readonly string _agentsDir = Path.Combine(stateDir, "agents");

    /// <summary>Atomically write (or overwrite) the record for its agent id. Creates the agents
    /// directory on first use. Throws on I/O failure — the caller (§6.4(2a)) treats a write failure as
    /// a launch failure and tears down the child.</summary>
    public void Write(AgentPidRecord record) {
        Directory.CreateDirectory(_agentsDir);

        var finalPath = PathFor(record.AgentId);
        var tempPath  = finalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, CapacitorJsonContext.Default.AgentPidRecord));
        File.Move(tempPath, finalPath, overwrite: true); // atomic same-directory rename
    }

    /// <summary>Delete an agent's record (idempotent — false if it wasn't there). Best-effort.</summary>
    public bool Delete(string agentId) {
        var path = PathFor(agentId);
        try {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        } catch (Exception ex) {
            logger.LogWarning(ex, "AgentPidRecordStore: failed to delete record for {AgentId}", agentId);
            return false;
        }
    }

    /// <summary>All parseable leftover records. An unparseable file is renamed <c>.json.corrupt</c>
    /// (retained for the operator, never acted on) and excluded from the result.</summary>
    public IReadOnlyList<AgentPidRecord> ReadAll() {
        if (!Directory.Exists(_agentsDir)) return [];

        var results = new List<AgentPidRecord>();

        foreach (var path in Directory.EnumerateFiles(_agentsDir, "*.json")) {
            AgentPidRecord record;
            try {
                record = JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.AgentPidRecord);
            } catch (Exception ex) {
                logger.LogWarning(ex, "AgentPidRecordStore: unparseable record {Path}; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            // A structurally-valid-JSON-but-empty record (no agent id) is also quarantined — it can
            // never authorize a kill and shouldn't linger as a live-looking record.
            if (string.IsNullOrEmpty(record.AgentId)) {
                logger.LogWarning("AgentPidRecordStore: record {Path} has no agent id; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            results.Add(record);
        }

        return results;
    }

    string PathFor(string agentId) => Path.Combine(_agentsDir, agentId + ".json");

    void TryQuarantine(string path) {
        try { File.Move(path, path + ".corrupt", overwrite: true); }
        catch (Exception ex) { logger.LogWarning(ex, "AgentPidRecordStore: failed to quarantine {Path}", path); }
    }
}
