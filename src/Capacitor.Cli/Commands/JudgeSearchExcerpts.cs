using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Cuts each search hit's snippet to a window around where the query matched, and drops trailing hits that would
/// take the page past <see cref="JudgeResultCap.MaxChars"/>. A server can return a hit's whole event as its snippet, and
/// twenty whole events overflow the judge's tool-result limit.</summary>
static class JudgeSearchExcerpts {
    const int Before = 200;
    const int Window = 500;

    public static string Render(string body, string query) {
        if (JsonNode.Parse(body) is not JsonObject root || root["results"] is not JsonArray results) return body;

        foreach (var hit in results.OfType<JsonObject>()) {
            if (hit["snippet"] is not JsonValue value || !value.TryGetValue<string>(out var snippet) || snippet.Length <= Window) continue;
            hit["snippet"] = JsonNode.Parse($"\"{JsonEncodedText.Encode(Excerpt(snippet, query))}\"");
        }

        var text    = root.ToJsonString();
        var omitted = 0;
        while (text.Length > JudgeResultCap.MaxChars && results.Count > 1) {
            results.RemoveAt(results.Count - 1);
            omitted++;
            root["results_omitted"] = omitted;
            text = root.ToJsonString();
        }

        return text;
    }

    static string Excerpt(string snippet, string query) {
        var start = JudgeText.Boundary(snippet, Math.Max(0, JudgeText.MatchAt(snippet, query) - Before));
        var end   = JudgeText.Boundary(snippet, Math.Min(snippet.Length, start + Window));

        return $"{(start > 0 ? "…" : "")}{snippet[start..end]}{(end < snippet.Length ? "…" : "")} ({snippet.Length} chars)";
    }
}
