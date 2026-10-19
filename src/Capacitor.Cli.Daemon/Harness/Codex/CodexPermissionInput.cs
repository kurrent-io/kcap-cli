using System.Buffers;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// Codex's permission prompt adds the escalation's justification to the call's input as a
/// top-level <c>description</c>, which the finished call's hook does not carry.
internal static class CodexPermissionInput {
    public static JsonElement MatchKey(JsonElement input) {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("description", out _)) return input;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            foreach (var property in input.EnumerateObject())
                if (property.Name != "description") property.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
