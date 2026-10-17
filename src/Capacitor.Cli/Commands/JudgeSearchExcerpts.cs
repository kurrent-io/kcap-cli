using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Cuts each search hit's snippet to a window around the first query term it contains. A server can return a hit's
/// whole event as its snippet, and twenty whole events overflow the judge's tool-result limit.</summary>
static class JudgeSearchExcerpts {
    const int Before = 200;
    const int Window = 500;

    public static string Render(string body, string query) {
        if (JsonNode.Parse(body) is not JsonObject root || root["results"] is not JsonArray results) return body;

        var terms = Terms(query);

        foreach (var hit in results.OfType<JsonObject>()) {
            if (hit["snippet"] is not JsonValue value || !value.TryGetValue<string>(out var snippet) || snippet.Length <= Window) continue;
            hit["snippet"] = JsonNode.Parse($"\"{JsonEncodedText.Encode(Excerpt(snippet, terms))}\"");
        }

        return root.ToJsonString();
    }

    static string Excerpt(string snippet, string[] terms) {
        var at = terms
            .Select(t => snippet.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, at - Before);
        var end   = Math.Min(snippet.Length, start + Window);

        return $"{(start > 0 ? "…" : "")}{snippet[start..end]}{(end < snippet.Length ? "…" : "")} ({snippet.Length} chars)";
    }

    // The words of a websearch-style query, without its quotes, negations and OR keywords.
    static string[] Terms(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '(', ')').TrimStart('-'))
            .Where(t => t.Length > 0 && !t.Equals("or", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
