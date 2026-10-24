using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// The `applyPermissions` payload the web UI sends for "Always allow", and what Claude reads it as. Claude saves a rule
/// only from a PermissionRequest decision's `updatedPermissions`, so the `toolAlwaysAllow` entry, which older servers
/// and clients still send, is translated where the hook response is built.
public static class ClaudePermissions {
    const string ToolAlwaysAllow = "toolAlwaysAllow";

    public static JsonElement AlwaysAllow(string toolName) {
        var json = JsonSerializer.Serialize(new[] { new AlwaysAllowEntry(ToolAlwaysAllow, toolName) },
            ClaudePermissionsJsonContext.Default.AlwaysAllowEntryArray);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// The rules Claude itself suggested for the call, or a session rule for the whole tool when it suggested none; null
    /// when the answer asks to save nothing.
    public static JsonArray? UpdatesFor(JsonElement? applyPermissions, JsonElement? permissionSuggestions) {
        if (applyPermissions is not { ValueKind: JsonValueKind.Array } entries) return null;

        var tools = entries.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.Object && Text(e, "type") == ToolAlwaysAllow)
            .Select(e => Text(e, "tool"))
            .OfType<string>()
            .Where(tool => tool.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tools.Count == 0) return null;

        if (permissionSuggestions is { ValueKind: JsonValueKind.Array } suggested && suggested.GetArrayLength() > 0)
            return JsonNode.Parse(suggested.GetRawText())!.AsArray();

        return new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject {
            ["type"]        = "addRules",
            ["rules"]       = new JsonArray(new JsonObject { ["toolName"] = tool }),
            ["behavior"]    = "allow",
            ["destination"] = "session"
        })]);
    }

    static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal sealed record AlwaysAllowEntry(string Type, string Tool);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudePermissions.AlwaysAllowEntry[]))]
internal partial class ClaudePermissionsJsonContext : JsonSerializerContext;
