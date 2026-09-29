using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// The time a payload carries for itself; null when the payload has none. A conversational payload
/// is stamped from its transcript record, a subagent completion when the server heard the stop.
public static class CanonicalEventTime {
    public static DateTimeOffset? Of(object payload) => payload switch {
        UserMessageReceived p         => p.Timestamp?.ToDateTimeOffset(),
        AssistantTextGenerated p      => p.Timestamp?.ToDateTimeOffset(),
        AssistantThinkingGenerated p  => p.Timestamp?.ToDateTimeOffset(),
        AssistantToolCallsGenerated p => p.Timestamp?.ToDateTimeOffset(),
        ToolResultReceived p          => p.Timestamp?.ToDateTimeOffset(),
        SubagentCompleted p           => p.Timestamp?.ToDateTimeOffset(),
        _                             => null,
    };
}
