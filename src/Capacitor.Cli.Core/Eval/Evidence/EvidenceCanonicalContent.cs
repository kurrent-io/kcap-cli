using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>What must reach the model before a wire event entry counts as delivered: the typed text, output or arguments of the
/// five content kinds, and the payload of every other kind. Metadata and envelope never count. The shared contract fixture
/// pins this table against the server's.</summary>
public static class EvidenceCanonicalContent {
    static readonly IReadOnlySet<string> ContentKinds = new HashSet<string>(StringComparer.Ordinal) {
        "user_message", "assistant_text", "assistant_thinking", "tool_call", "tool_result"
    };

    public static bool IsContentKind(string? kind) => kind is not null && ContentKinds.Contains(kind);

    /// <summary>Arguments and the whole call at one ordinal deliver the same content, so they share a key.</summary>
    public static string BodyKey(string reference, string field, int? ordinal) =>
        $"{reference}|{(field is "arguments" or "call" ? "call" : field)}|{ordinal ?? 0}";

    /// <summary>The canonical bodies a wire entry left as descriptors.</summary>
    public static IReadOnlyList<(string Ref, string Field, int? Ordinal)> Deferred(JsonElement entry) {
        var bodies    = new List<(string Ref, string Field, int? Ordinal)>();
        var reference = entry.Str("ref") ?? "";
        switch (entry.Str("kind")) {
            case "user_message" or "assistant_text" or "assistant_thinking":
                // Encrypted thinking carries neither text nor a text body.
                if (entry.Str("text") is null && Descriptor(entry, "text_body") is { } text) bodies.Add(text);
                break;
            case "tool_call":
                var listed = 0;
                if (entry.Arr("calls") is { } calls)
                    foreach (var call in calls.EnumerateArray()) {
                        listed++;
                        if (IsAbsent(call, "arguments") && Descriptor(call, "arguments_body") is { } arguments) bodies.Add(arguments);
                    }
                for (var ordinal = listed; ordinal < (entry.Num("calls_total") ?? 0); ordinal++) bodies.Add((reference, "call", ordinal));
                break;
            case "tool_result":
                if (entry.Str("output") is null && Descriptor(entry, "output_body") is { } output) bodies.Add(output);
                break;
            default:
                if (Descriptor(entry, "payload_body") is { } payload) bodies.Add(payload);
                break;
        }
        return bodies;
    }

    static bool IsAbsent(JsonElement e, string property) => e.Prop(property) is not { } v || v.IsNull;

    static (string Ref, string Field, int? Ordinal)? Descriptor(JsonElement e, string property) =>
        e.Obj(property) is { } d && d.Str("ref") is { } r && d.Str("field") is { } f ? (r, f, d.Num("ordinal") is { } o ? (int)o : null) : null;
}
