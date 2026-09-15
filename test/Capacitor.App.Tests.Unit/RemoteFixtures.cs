using System.Text.Json;
using Capacitor.Remote.Models;
using Eventuous.SignalR;

namespace Capacitor.App.Tests.Unit;

/// Server-shaped session events for the remote chat: a detail document and a live envelope.
static class RemoteFixtures {
    public static SessionDetailDto Detail(params SessionEventDto[] events) => new() {
        SessionId = "s1", LastEventNumber = events.Length == 0 ? -1 : events[^1].EventNumber, Events = events,
    };

    public static SessionEventDto Event(long number, string type, string payloadJson) => new() {
        EventType = type, EventNumber = number, Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
    };

    public static StreamEventEnvelope Envelope(string sessionId, ulong position, string type, string payloadJson) => new() {
        EventId = Guid.NewGuid(), Stream = StreamNames.AgentSession(sessionId), EventType = type,
        StreamPosition = position, GlobalPosition = position, Timestamp = DateTime.UtcNow, JsonPayload = payloadJson,
    };

    public const string Hello = """{"content":"hello"}""";
    public const string HiThere = """{"content":"Hi there"}""";
    public const string LsCall = """{"tool_calls":[{"call_id":"t1","tool_name":"Bash","arguments":{"command":"ls"}}]}""";
}
