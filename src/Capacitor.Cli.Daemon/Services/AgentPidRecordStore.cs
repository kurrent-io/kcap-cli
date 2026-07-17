using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Durable per-agent PID records under <c>{state-dir}/agents/</c>, so a restarted daemon can reap a
/// surviving child by exact identity (spec §6.4(2)). Written atomically (temp file + same-directory
/// rename). The filename is a SHA-256 of the agent id, NOT the id itself: the id crosses the SignalR
/// wire unconstrained, so using it directly in a path would let <c>..</c> / separators escape the
/// directory. The original id is preserved inside the JSON. Deleted only after confirmed death (caller's
/// rule); an unparseable/empty file is quarantined (renamed <c>.corrupt</c>) and never acted on.
/// </summary>
internal sealed class AgentPidRecordStore(string stateDir, ILogger logger) {
    readonly string _agentsDir = Path.Combine(stateDir, "agents");

    /// <summary>Atomically write (or overwrite) the record. Throws on I/O failure — the caller treats a
    /// write failure as a launch failure and tears the child down (§6.4(2a)).</summary>
    public void Write(AgentPidRecord record) {
        Directory.CreateDirectory(_agentsDir);

        var finalPath = PathFor(record.AgentId);
        var tempPath  = finalPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, CapacitorJsonContext.Default.AgentPidRecord));
        File.Move(tempPath, finalPath, overwrite: true); // atomic same-directory rename
    }

    /// <summary>Delete an agent's record (idempotent — false if absent). Best-effort.</summary>
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

    /// <summary>All parseable leftover records. An unparseable or agent-id-less file is renamed
    /// <c>.corrupt</c> (retained for the operator, never acted on) and excluded.</summary>
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

            if (string.IsNullOrEmpty(record.AgentId)) {
                logger.LogWarning("AgentPidRecordStore: record {Path} has no agent id; quarantining as .corrupt", path);
                TryQuarantine(path);
                continue;
            }

            results.Add(record);
        }

        return results;
    }

    // Hash the (untrusted) agent id into the filename so no id can escape _agentsDir via path separators.
    string PathFor(string agentId) => Path.Combine(_agentsDir, SafeName(agentId) + ".json");

    static string SafeName(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant();

    void TryQuarantine(string path) {
        try { File.Move(path, path + ".corrupt", overwrite: true); }
        catch (Exception ex) { logger.LogWarning(ex, "AgentPidRecordStore: failed to quarantine {Path}", path); }
    }
}
