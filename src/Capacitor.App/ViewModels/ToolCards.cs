using System.Text.Json;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// Builds a card from a settled call. A page card needs the server's whole artefact object in the
/// result; the result rides the transcript capped at 4096 characters, so a cut body builds nothing
/// and the call stays a row.
public static class ToolCards {
    const int NameCap = 80;

    public static ToolCard? Build(ToolCardKind kind, string? inputJson, string? resultText) {
        resultText = McpText(resultText);
        return kind switch {
            ToolCardKind.Page     => Page(resultText),
            ToolCardKind.Document => Document(inputJson),
            ToolCardKind.Flow     => Flow(inputJson),
            ToolCardKind.Agent    => Agent(inputJson, resultText),
            _                     => null,
        };
    }

    public static string Audience(string? visibility) => visibility switch {
        "org"    => "Org",
        "scoped" => "Shared",
        _        => "Private",
    };

    /// The declaring machine's path, so the name is cut on either separator.
    public static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    static ToolCard? Page(string? resultText) {
        if (string.IsNullOrWhiteSpace(resultText)) return null;
        try {
            using var doc = JsonDocument.Parse(resultText);
            if (!doc.RootElement.IsObject) return null;
            // The pages tool names the object `page`; a recorded result may carry the server's `artefact`.
            if ((doc.RootElement.Obj("page") ?? doc.RootElement.Obj("artefact")) is not { } page) return null;
            var title = page.Str("title");
            var url = page.Str("url");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) return null;
            var version = page.Num("latest_version") ?? 1;
            return new ToolCard(ToolCardKind.Page, version > 1 ? "Updated page" : "Published page",
                TextElision.End(title, NameCap), $"v{version} · {Audience(page.Str("visibility"))}", url, null);
        } catch (JsonException) {
            return null;
        }
    }

    static ToolCard? Document(string? inputJson) {
        if (Args(inputJson) is not { } args) return null;
        var path = args.Str("path");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var title = args.Str("kind") switch {
            "plan"   => "Declared plan",
            "spec"   => "Declared spec",
            "design" => "Declared design doc",
            _        => "Declared document",
        };
        return new ToolCard(ToolCardKind.Document, title, FileName(path), path, null, path);
    }

    static ToolCard? Flow(string? inputJson) {
        if (Args(inputJson) is not { } args) return null;
        var kind = args.Str("kind") ?? args.Str("definition_id") ?? "";
        var title = kind.Length == 0 ? "Started flow" : $"Started {kind} flow";
        return new ToolCard(ToolCardKind.Flow, title, kind, args.Str("vendor") ?? "", null, null);
    }

    static ToolCard? Agent(string? inputJson, string? resultText) {
        if (Args(inputJson) is not { } args) return null;
        var prompt = args.Str("prompt") ?? "";
        var firstLine = prompt.Split(['\r', '\n'], 2)[0].Trim();
        return new ToolCard(ToolCardKind.Agent, "Started hosted agent", TextElision.End(firstLine, NameCap), args.Str("vendor") ?? "",
            ResultField(resultText, "url") is { } url && LinkPolicy.IsOpenable(url) ? url : null, null);
    }

    /// The text blocks of an MCP result object, which a Codex driver records whole; any other result as given.
    static string? McpText(string? resultText) {
        if (string.IsNullOrEmpty(resultText) || resultText[0] != '{') return resultText;
        try {
            using var doc = JsonDocument.Parse(resultText);
            if (doc.RootElement.Arr("content") is not { } content) return resultText;
            var texts = content.EnumerateArray().Where(b => b.Str("type") == "text").Select(b => b.Str("text")).OfType<string>().ToArray();
            return texts.Length == 0 ? resultText : string.Concat(texts);
        } catch (JsonException) {
            return resultText;
        }
    }

    /// A `key: value` line of a `start_agent` result.
    static string? ResultField(string? resultText, string key) {
        if (string.IsNullOrEmpty(resultText)) return null;
        var prefix = key + ": ";
        foreach (var line in resultText.Split('\n')) {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var value = line[prefix.Length..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    static JsonElement? Args(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return null;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            return doc.RootElement.IsObject ? doc.RootElement.Clone() : null;
        } catch (JsonException) {
            return null;
        }
    }
}
