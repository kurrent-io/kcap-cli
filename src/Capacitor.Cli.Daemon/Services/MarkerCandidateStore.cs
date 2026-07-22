using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Phase B2-b (sequenced-settlement design §4.2.4): durable marker-candidate sources for
/// RECORDLESS prior-epoch survivors. Kill authority is marker-based (the live env triple, re-read at
/// kill time); there is NO spawn-bound start-identity and NO trusted flow identity (the env is
/// mutable). Same atomic temp+rename + hashed-filename discipline as
/// <see cref="AgentPidRecordStore"/>.</summary>
internal sealed partial class MarkerCandidateStore(string stateDir, ILogger logger) {
    readonly string _dir = Path.Combine(stateDir, "marker-candidates");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(MarkerCandidate))]
    partial class MarkerCandidateJsonCtx : JsonSerializerContext;

    public void Write(MarkerCandidate c) {
        Directory.CreateDirectory(_dir);
        var final = PathFor(c.AgentId);
        var tmp   = final + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, MarkerCandidateJsonCtx.Default.MarkerCandidate));
        File.Move(tmp, final, overwrite: true);
    }

    public bool Delete(string agentId) {
        try { var p = PathFor(agentId); if (!File.Exists(p)) return false; File.Delete(p); return true; }
        catch (Exception ex) { logger.LogWarning(ex, "MarkerCandidateStore: delete failed for {AgentId}", agentId); return false; }
    }

    public IReadOnlyList<MarkerCandidate> ReadAll() {
        if (!Directory.Exists(_dir)) return [];
        var r = new List<MarkerCandidate>();
        foreach (var p in Directory.EnumerateFiles(_dir, "*.json")) {
            try {
                var c = JsonSerializer.Deserialize(File.ReadAllText(p), MarkerCandidateJsonCtx.Default.MarkerCandidate);
                if (!string.IsNullOrEmpty(c.AgentId)) r.Add(c);
            } catch (Exception ex) { logger.LogWarning(ex, "MarkerCandidateStore: unparseable source {Path}", p); }
        }
        return r;
    }

    string PathFor(string agentId) => Path.Combine(_dir,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId ?? ""))).ToLowerInvariant() + ".json");
}

/// <summary>Phase B2-b (sequenced-settlement design §4.2.4): the durable marker-candidate source
/// tuple. Marker-based kill authority for a fully recordless prior-epoch survivor — no start-identity
/// token, no trusted flow identity.</summary>
internal readonly record struct MarkerCandidate(string AgentId, string DaemonId, string OldEpoch, int Pid);
