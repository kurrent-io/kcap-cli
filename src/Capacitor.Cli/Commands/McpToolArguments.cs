using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Shape checks for MCP tool arguments. Only SHAPE is validated here — a present-but-wrong
/// typed argument must fail loudly rather than be dropped — while the rules the server owns
/// (vocabularies, cross-entity constraints) surface as its coded 4xx bodies.</summary>
static class McpToolArguments {
    /// <summary>A required non-blank string; absent, null, blank or wrong-typed throws the clean
    /// tool-error shape.</summary>
    internal static string RequireString(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) throw new ArgumentException($"'{key}' is required.");

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value))
            throw new ArgumentException($"'{key}' must be a string.");

        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{key}' must not be blank.");

        return value;
    }

    /// <summary>An optional string: absent, JSON null or blank is null (trimmed otherwise); a present
    /// non-string throws.</summary>
    internal static string? OptionalString(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) return null;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value))
            throw new ArgumentException($"'{key}' must be a string.");

        var trimmed = value.Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Reads a numeric field as int. Returns false ONLY when the key is absent or JSON null;
    /// any PRESENT non-integer shape (string, object, array, fractional or out-of-range number)
    /// throws, so a malformed selector fails instead of degrading into a differently-shaped
    /// request. Wire JSON is validated against the RAW token via TryGetInt32 — exact, no lossy
    /// double round-trip; the int/long branches cover programmatically constructed nodes.</summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        if (node is JsonValue v) {
            if (v.TryGetValue<JsonElement>(out var el)) {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;

                throw new ArgumentException($"'{key}' must be an integer within int range.");
            }

            if (v.TryGetValue(out value)) return true;

            if (v.TryGetValue<long>(out var lv)) {
                if (lv is < int.MinValue or > int.MaxValue)
                    throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

                value = (int)lv;

                return true;
            }
        }

        throw new ArgumentException($"'{key}' must be an integer.");
    }
}
