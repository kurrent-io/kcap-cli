using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// What a rollout carries between lines. The turn model, the usage cluster and the web-search
/// pairing span batches, because a cluster's `token_count` and a search's `web_search_end` can land
/// in another flush than the events they describe; the telemetry stashes and the index of results
/// returned this batch are cleared at every batch boundary.
public sealed class CodexRolloutContext : TranscriptContext {
    internal string? Model { get; set; }

    /// Every reasoning, assistant text, agent message and tool call since the last finalization.
    internal List<ClusterMember> Cluster { get; } = [];

    /// Set when a token_count produced usage; the next member then opens a new cluster.
    internal bool ClusterFinalized { get; set; }

    internal Queue<string> PendingWebSearchCallIds { get; } = new();

    /// Every id ever queued, so a re-delivered `web_search_end` cannot queue it twice and pair a
    /// later search with the wrong id.
    internal HashSet<string> EnqueuedWebSearchCallIds { get; } = new(StringComparer.Ordinal);

    /// The id each search line was given, so a re-delivered line gets the same one back after the
    /// queue has drained.
    internal Dictionary<Guid, string> AssignedWebSearchCallIds { get; } = [];

    internal Dictionary<string, CodexExecResultExtension>  PendingExec    { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CodexPatchResultExtension> PendingPatch   { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Guid>                      EmittedResults { get; } = new(StringComparer.Ordinal);

    public override void BeginBatch() {
        PendingExec.Clear();
        PendingPatch.Clear();
        EmittedResults.Clear();
    }

    internal void Track(CanonicalEvent evt) {
        if (Cluster.Exists(m => m.EventId == evt.EventId)) return;
        if (ClusterFinalized) {
            Cluster.Clear();
            ClusterFinalized = false;
        }
        var toolName = evt.Payload is AssistantToolCallsGenerated calls && calls.ToolCalls.Count > 0 ? calls.ToolCalls[0].ToolName : null;
        Cluster.Add(new(evt.EventId, evt.EventType, toolName));
    }

    internal readonly record struct ClusterMember(Guid EventId, string EventType, string? ToolName);
}
