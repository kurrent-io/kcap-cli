using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// The time a payload carries for itself, which both the leaf and the server's normalizer stamp
/// from the transcript record; null when the payload has none.
public static class CanonicalEventTime {
    public static DateTimeOffset? Of(object payload) => payload switch {
        UserMessageReceived p         => p.Timestamp?.ToDateTimeOffset(),
        AssistantTextGenerated p      => p.Timestamp?.ToDateTimeOffset(),
        AssistantThinkingGenerated p  => p.Timestamp?.ToDateTimeOffset(),
        AssistantToolCallsGenerated p => p.Timestamp?.ToDateTimeOffset(),
        ToolResultReceived p          => p.Timestamp?.ToDateTimeOffset(),
        _                             => null,
    };
}
