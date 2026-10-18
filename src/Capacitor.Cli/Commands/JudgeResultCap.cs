using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>Bounds every judge tool result below what Claude Code returns inline (25,000 tokens by default). Past that it
/// spills the result to a file and tells the model to read it, and the judge has no tool that reads files.</summary>
static class JudgeResultCap {
    public const int MaxChars = 40_000;

    /// <summary>Leaves non-ASCII text unescaped, so a page's length tracks the characters it carries.</summary>
    public static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The text as is when it fits; otherwise its head and a note saying how much was cut and how to narrow the call.</summary>
    public static string Cap(string text, string hint) =>
        text.Length <= MaxChars
            ? text
            : $"{text[..JudgeText.Boundary(text, MaxChars)]}\n\n[truncated: showing {MaxChars} of {text.Length} characters. {hint}]";

    /// <summary>One page of the text from <paramref name="offset"/>, with a note naming the next offset when more follows.</summary>
    public static string Page(string text, int offset) {
        if (offset == 0 && text.Length <= MaxChars) return text;
        if (offset >= text.Length) return $"[offset {offset} is past the end: the result has {text.Length} characters]";

        var start = JudgeText.Boundary(text, offset);
        var end   = JudgeText.Boundary(text, Math.Min(text.Length, start + MaxChars));

        return $"{text[start..end]}\n\n[characters {start}-{end} of {text.Length}.{(end < text.Length ? $" Call again with offset {end} for the rest." : "")}]";
    }

    /// <summary>The tool-result envelope with its <c>output</c> paged, so offsets count characters of the output itself and
    /// every page stays one JSON document within <see cref="MaxChars"/> once escaped. A body that is not the envelope is paged
    /// as text.</summary>
    public static string PageOutput(string body, int offset) {
        if (offset == 0 && body.Length <= MaxChars) return body;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException) { return Page(body, offset); }

        using (doc) {
            var root = doc.RootElement;
            if (root.Str("output") is not { } output) return Page(body, offset);
            if (offset >= output.Length) return $"[offset {offset} is past the end: the output has {output.Length} characters]";

            var start = JudgeText.Boundary(output, offset);
            var least = Math.Min(output.Length, start + (char.IsHighSurrogate(output[start]) ? 2 : 1));
            var end   = JudgeText.Boundary(output, Math.Min(output.Length, start + MaxChars));

            // Escaping can grow a page past the cap, so shrink by the overflow until it fits; each output character dropped
            // removes at least one escaped character.
            while (true) {
                var page = Envelope(root, output, start, end);
                if (page.Length <= MaxChars || end <= least) return page;
                end = Math.Max(least, JudgeText.Boundary(output, end - (page.Length - MaxChars)));
            }
        }
    }

    static string Envelope(JsonElement root, string output, int start, int end) {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Writer)) {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject().Where(p => p.Name != "output")) p.WriteTo(w);
            w.WriteNumber("output_chars", output.Length);
            w.WriteNumber("offset", start);
            if (end < output.Length) w.WriteNumber("next_offset", end);
            w.WriteString("output", output[start..end]);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
