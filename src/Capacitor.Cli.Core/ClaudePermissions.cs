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

    /// The rules Claude suggested for the call's own tool, or a session rule for that tool when it suggested none; null
    /// when the answer approves no tool, or one other than the call's.
    public static JsonArray? UpdatesFor(JsonElement? applyPermissions, JsonElement? permissionSuggestions, string? toolName) {
        if (string.IsNullOrEmpty(toolName) || applyPermissions is not { ValueKind: JsonValueKind.Array } entries) return null;

        var approved = entries.EnumerateArray()
            .Any(e => e.ValueKind == JsonValueKind.Object && Text(e, "type") == ToolAlwaysAllow && Text(e, "tool") == toolName);

        if (!approved) return null;

        var suggested = permissionSuggestions is { ValueKind: JsonValueKind.Array } all ? ForTool(all, toolName) : [];

        if (suggested.Count > 0) return suggested;

        return [
            new JsonObject {
                ["type"]        = "addRules",
                ["rules"]       = new JsonArray(new JsonObject { ["toolName"] = toolName }),
                ["behavior"]    = "allow",
                ["destination"] = "session"
            }
        ];
    }

    /// The suggested rule updates, each cut to its rules for the tool. Mode and directory changes name no tool, so none
    /// of them is approved.
    static JsonArray ForTool(JsonElement suggestions, string toolName) {
        var kept = new JsonArray();

        foreach (var suggestion in suggestions.EnumerateArray()) {
            if (suggestion.ValueKind != JsonValueKind.Object
             || !suggestion.TryGetProperty("rules", out var rules)
             || rules.ValueKind != JsonValueKind.Array)
                continue;

            var own = rules.EnumerateArray()
                .Where(rule => rule.ValueKind == JsonValueKind.Object && Text(rule, "toolName") == toolName)
                .ToList();

            if (own.Count == 0) continue;

            var update = JsonNode.Parse(suggestion.GetRawText())!.AsObject();
            update["rules"] = new JsonArray([.. own.Select(rule => JsonNode.Parse(rule.GetRawText()))]);
            kept.Add((JsonNode)update);
        }

        return kept;
    }

    static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal sealed record AlwaysAllowEntry(string Type, string Tool);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudePermissions.AlwaysAllowEntry[]))]
internal partial class ClaudePermissionsJsonContext : JsonSerializerContext;
