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

    /// <summary>A required non-blank string, trimmed, of at most <paramref name="maxLength"/> characters.</summary>
    internal static string RequireBoundedString(JsonObject? args, string key, int maxLength) {
        var value = RequireString(args, key).Trim();

        if (value.Length > maxLength)
            throw new ArgumentException($"'{key}' is {value.Length} characters; the limit is {maxLength}.");

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

    /// <summary>An optional boolean: absent or JSON null is null; a present non-boolean throws.</summary>
    internal static bool? OptionalBool(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) return null;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<bool>(out var value))
            throw new ArgumentException($"'{key}' must be a boolean.");

        return value;
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
                if (el.IsNumber && el.TryGetInt32(out value)) return true;

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

    /// <summary>Reads a numeric field as long, false only when absent or JSON null. Besides a JSON
    /// integer it takes a canonical integer string (<c>^-?(0|[1-9]\d*)$</c>), the other shape the
    /// server's int64 fields are declared with, so a token copied verbatim from a response round-trips.
    /// Any other present shape throws.</summary>
    internal static bool TryReadLong(JsonObject? args, string key, out long value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        if (node is JsonValue v) {
            if (v.TryGetValue<JsonElement>(out var el)) {
                if (el.IsNumber && el.TryGetInt64(out value)) return true;
                if (el.IsString && TryParseCanonical(el.GetString(), out value)) return true;
            } else if (v.TryGetValue(out value)) {
                return true;
            } else if (v.TryGetValue<int>(out var iv)) {
                value = iv;
                return true;
            } else if (v.TryGetValue<string>(out var sv) && TryParseCanonical(sv, out value)) {
                return true;
            }
        }

        throw new ArgumentException($"'{key}' must be an integer.");
    }

    static bool TryParseCanonical(string? s, out long value) {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;

        var digits = s[0] == '-' ? s.AsSpan(1) : s.AsSpan();
        if (digits.IsEmpty || (digits[0] == '0' && digits.Length > 1)) return false;
        foreach (var c in digits)
            if (c is < '0' or > '9') return false;

        return long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
