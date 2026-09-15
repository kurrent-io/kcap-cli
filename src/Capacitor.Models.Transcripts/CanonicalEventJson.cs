using Google.Protobuf;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// Reads a persisted conversational payload back into its schema message. Unknown fields are
/// ignored: a newer server may add one, and the client must still read the event.
public static class CanonicalEventJson {
    static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// Null for a type the chat does not render or a payload that does not parse as it.
    public static object? TryParse(string eventType, string json) {
        try {
            return eventType switch {
                CanonicalEventTypes.UserMessageReceived         => Parser.Parse<UserMessageReceived>(json),
                CanonicalEventTypes.AssistantTextGenerated      => Parser.Parse<AssistantTextGenerated>(json),
                CanonicalEventTypes.AssistantThinkingGenerated  => Parser.Parse<AssistantThinkingGenerated>(json),
                CanonicalEventTypes.AssistantToolCallsGenerated => Parser.Parse<AssistantToolCallsGenerated>(json),
                CanonicalEventTypes.ToolResultReceived          => Parser.Parse<ToolResultReceived>(json),
                _ => null,
            };
        } catch (InvalidJsonException) {
            return null;
        } catch (InvalidProtocolBufferException) {
            return null;
        }
    }
}
