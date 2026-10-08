using System.Buffers;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.MistralVibe;

/// <summary>
/// Carries Vibe's token usage onto the transcript. Vibe keeps only a running total for the session,
/// so the tokens counted since the previous stamp go on the latest finished assistant message, with
/// the running total alongside so the next stamp knows where this one left off. Whatever the
/// cadence of syncs, the stamps add up to Vibe's own total.
/// </summary>
internal static class MistralVibeUsageStamp {
    public const string Key = "kcapUsage";

    public static IReadOnlyList<string> Apply(
            IReadOnlyList<string> lines, MistralVibeTokenUsage since, MistralVibeTokenUsage? total, string? model) {
        if (total is null || !total.Exceeds(since)) return lines;

        var index = LastAssistantMessage(lines);
        if (index < 0) return lines;

        var stamped = lines.ToList();
        stamped[index] = Stamp(lines[index], total.Minus(since), total, model);
        return stamped;
    }

    /// <summary>The running total the last stamp among <paramref name="lines"/> recorded, or zero.</summary>
    public static MistralVibeTokenUsage LastStamped(IEnumerable<string> lines) {
        var last = MistralVibeTokenUsage.Zero;
        foreach (var line in lines) {
            if (!line.Contains(Key, StringComparison.Ordinal)) continue;
            try {
                using var doc = JsonDocument.Parse(line);
                if (MistralVibeTokenUsage.From(doc.RootElement.Obj(Key)?.Obj("cumulative")) is { } cumulative) last = cumulative;
            } catch (JsonException) { }
        }
        return last;
    }

    static int LastAssistantMessage(IReadOnlyList<string> lines) {
        for (var i = lines.Count - 1; i >= 0; i--) {
            try {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.Str("type") == "message" && doc.RootElement.Str("role") == "assistant") return i;
            } catch (JsonException) { }
        }
        return -1;
    }

    static string Stamp(string line, MistralVibeTokenUsage delta, MistralVibeTokenUsage total, string? model) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            delta.WriteTo(writer);
            if (model is not null) writer.WriteString("model", model);
            writer.WriteStartObject("cumulative");
            total.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var end = line.LastIndexOf('}');
        return $"{line[..end]},\"{Key}\":{Encoding.UTF8.GetString(buffer.WrittenSpan)}}}";
    }
}
