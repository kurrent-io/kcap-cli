using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A PermissionRequested push. Options is null for a Claude PTY prompt; an ACP permission
/// carries the agent's own options, and the answer must name one by OptionId.
public sealed record ServerPermissionRequest(
        string SessionId, string RequestId, string ToolName, JsonElement? ToolInput,
        IReadOnlyList<AcpInteractionOption>? Options) {

    /// The wire sends tool_input either as an object or as its JSON text, and the options slot
    /// either as null or an array of option objects; anything else reads as absent.
    public static ServerPermissionRequest From(string sessionId, string requestId, string? toolName, JsonElement? toolInput, JsonElement? options) =>
        new(sessionId, requestId, toolName ?? "", NormalizeInput(toolInput), ParseOptions(options));

    internal static JsonElement? NormalizeInput(JsonElement? input) {
        if (input is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String) {
            try { using var doc = JsonDocument.Parse(el.GetString()!); return doc.RootElement.Clone(); }
            catch (JsonException) { return el; }
        }
        return el.ValueKind == JsonValueKind.Null ? null : el;
    }

    internal static IReadOnlyList<AcpInteractionOption>? ParseOptions(JsonElement? options) {
        if (options is not { ValueKind: JsonValueKind.Array } arr) return null;
        try {
            var parsed = arr.Deserialize(RemoteModelsJsonContext.Default.AcpInteractionOptionArray);
            return parsed is { Length: > 0 } && parsed.All(o => !string.IsNullOrEmpty(o.OptionId)) ? parsed : null;
        } catch (JsonException) {
            return null;
        }
    }
}
