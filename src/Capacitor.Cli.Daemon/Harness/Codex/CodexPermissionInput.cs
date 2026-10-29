using System.Buffers;
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// Codex's permission prompt for a shell call adds the escalation's justification to the input as
/// a top-level <c>description</c>, which the finished call's hook does not carry. Every other tool's
/// input reaches the prompt unchanged, so a <c>description</c> there is the tool's own argument.
internal static class CodexPermissionInput {
    public static JsonElement MatchKey(string toolName, JsonElement input) {
        if (toolName != "Bash" || !input.IsObject || !input.TryGetProperty("description", out _)) return input;

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
