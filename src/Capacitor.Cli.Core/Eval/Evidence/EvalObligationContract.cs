using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>Mirrors the server's obligation contract: what the reporting judge may write in <c>obligations</c> and the caps
/// that bound it. An array breaking any cap is dropped whole; the verdict beside it is kept.</summary>
public static class EvalObligationContract {
    public const int MaxObligations = 50;
    public const int MaxTextBytes   = 256;
    public const int MaxTokenBytes  = 16;
    public const int MaxCitations   = 4;

    // 186 = one entry's keys, punctuation, longest vocabulary words ("scope_change", "unverified"), anchor and four tokens.
    public const int MaxEncodedBytes = MaxObligations * (186 + MaxTextBytes + MaxTextBytes) + (MaxObligations - 1) + 2;

    public const string Unverified = "unverified";

    public static readonly FrozenSet<string> Origins  = new[] { "plan", "request", "scope_change" }.ToFrozenSet(StringComparer.Ordinal);
    public static readonly FrozenSet<string> Statuses = new[] { "verified", "claimed", "not_done", "dropped", Unverified }.ToFrozenSet(StringComparer.Ordinal);

    // Both texts are the server's, byte for byte.
    public const string ReporterMarker =
        "REPORTING QUESTION: you are this run's reporting question. Besides your verdict, report `obligations` as the verdict contract describes: "
      + "what the declared plan, the user's requests and any change of scope owed.";

    public const string ResponseSchema =
        "\"obligations\" (optional; reporting question only): an array of at most 50 entries, each "
      + "{\"title\": string of at most 256 bytes, \"origin\": \"plan\" | \"request\" | \"scope_change\", "
      + "\"status\": \"verified\" | \"claimed\" | \"not_done\" | \"dropped\" | \"unverified\", "
      + "\"anchor\": one cite handle - the one place the evidence states this obligation, "
      + "\"citations\": at most 4 cite handles that support the status, never the anchor, "
      + "\"note\": optional string of at most 256 bytes}. No other members. "
      + "verified = you saw proof it was met. claimed = the agent said it was met and you found no independent proof (cite where it said so). "
      + "not_done = you saw evidence it was not met. dropped = a later change of scope removed it (cite that change). "
      + "unverified = you could not examine the proof; it needs no citation. Every other status needs at least one citation. "
      + "An obligation whose anchor you were never shown is discarded. Omit the member rather than send an empty array.";

    /// <summary>The <c>obligations</c> property of the reporting question's verdict schema, carrying the contract text as its
    /// description. Byte caps are not expressible here; <see cref="TryParse"/> enforces them.</summary>
    public static readonly string ObligationsJsonSchema =
        "{\"type\":\"array\",\"maxItems\":" + MaxObligations + ",\"description\":\"" + JsonEncodedText.Encode(ResponseSchema, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\",\"items\":{\"type\":\"object\",\"properties\":{"
      + "\"title\":{\"type\":\"string\",\"minLength\":1},\"origin\":{\"type\":\"string\",\"enum\":[\"plan\",\"request\",\"scope_change\"]},"
      + "\"status\":{\"type\":\"string\",\"enum\":[\"verified\",\"claimed\",\"not_done\",\"dropped\",\"unverified\"]},"
      + "\"anchor\":{\"type\":\"string\",\"minLength\":1},\"citations\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"minLength\":1},\"maxItems\":" + MaxCitations + "},"
      + "\"note\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"title\",\"origin\",\"status\",\"anchor\"],\"additionalProperties\":false}}";

    static readonly FrozenSet<string> Members = new[] { "title", "origin", "status", "anchor", "citations", "note" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The parsed entries, or null when the value is not an array this contract accepts: drop the array, keep the verdict.</summary>
    public static IReadOnlyList<EvalReportedObligation>? TryParse(JsonElement obligations) {
        if (!obligations.IsArray || obligations.GetArrayLength() > MaxObligations) return null;

        var result = new List<EvalReportedObligation>(obligations.GetArrayLength());
        foreach (var entry in obligations.EnumerateArray()) {
            if (ParseEntry(entry) is not { } parsed) return null;
            result.Add(parsed);
        }
        return result;
    }

    static EvalReportedObligation? ParseEntry(JsonElement entry) {
        if (!entry.IsObject) return null;

        string? title = null, origin = null, status = null, anchor = null, note = null;
        List<string>? citations = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in entry.EnumerateObject()) {
            if (!Members.Contains(member.Name) || !seen.Add(member.Name)) return null;
            var value = member.Value;
            switch (member.Name) {
                case "title":
                    if (!Text(value, out title) || string.IsNullOrWhiteSpace(title)) return null;
                    break;
                case "note":
                    if (value.IsNull) break;
                    if (!Text(value, out note)) return null;
                    break;
                case "origin":
                    if (!value.IsString || !Origins.Contains(origin = value.GetString()!)) return null;
                    break;
                case "status":
                    if (!value.IsString || !Statuses.Contains(status = value.GetString()!)) return null;
                    break;
                case "anchor":
                    if (!Token(value, out anchor)) return null;
                    break;
                case "citations":
                    if (!value.IsArray || value.GetArrayLength() > MaxCitations) return null;
                    citations = [];
                    foreach (var c in value.EnumerateArray()) {
                        if (!Token(c, out var token)) return null;
                        citations.Add(token!);
                    }
                    break;
            }
        }

        if (title is null || origin is null || status is null || anchor is null) return null;
        return new(title, origin, status, anchor, (IReadOnlyList<string>?)citations ?? [], note);
    }

    // The cap is on the bytes as encoded — between the quotes, escapes included — which is what bounds the payload.
    static bool Text(JsonElement value, out string? text) {
        text = null;
        if (!value.IsString || EncodedBytes(value) > MaxTextBytes) return false;
        text = value.GetString();
        return text is not null;
    }

    static bool Token(JsonElement value, out string? token) {
        token = null;
        if (!value.IsString || EncodedBytes(value) is < 1 or > MaxTokenBytes) return false;
        token = value.GetString();
        return !string.IsNullOrEmpty(token);
    }

    static int EncodedBytes(JsonElement s) => Encoding.UTF8.GetByteCount(s.GetRawText()) - 2;
}
