using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

/// <summary>The whole projection of a synthetic transcript that exercises every server rule at once —
/// usage and its memory across records, multi-block and multi-result records, a rejected line, images,
/// origin fields and the fallback id — compared against a reviewed golden file. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the golden after a deliberate change, then review the diff.</summary>
public class ClaudeGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Test]
    public async Task The_parity_fixture_projects_to_its_golden_file() {
        var dir      = Path.Combine(SourceDir(), "Fixtures");
        var lines    = await File.ReadAllLinesAsync(Path.Combine(dir, "claude-parity.jsonl"));
        var ctx      = ClaudeTranscriptEvents.Instance.CreateContext("sess", null);
        var projected = new JsonArray();

        for (var i = 0; i < lines.Length; i++) {
            var result = ClaudeTranscriptEvents.Instance.Project(lines[i], i + 1, Received, ctx);
            projected.Add(new JsonObject {
                ["line"]     = i + 1,
                ["rejected"] = result.Rejected is null ? null : JsonValue.Create(true),
                ["events"]   = new JsonArray([.. result.Events.Select(Render)]),
            });
        }

        var actual   = projected.ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(dir, "claude-parity.expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]           = e.EventType,
        ["id"]             = e.EventId.ToString(),
        ["causedBy"]       = e.CausedBy,
        ["recordTimestamp"] = e.RecordTimestamp,
        ["payload"]        = JsonNode.Parse(JsonFormatter.Default.Format((IMessage)e.Payload)),
        ["usage"]          = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
        ["cacheCreation"]  = e.CacheCreationTokens,
        ["attachments"]    = e.Attachments is null ? null : new JsonArray([.. e.Attachments.Select(a => (JsonNode)new JsonObject {
            ["id"] = a.Id.ToString(), ["fileName"] = a.FileName, ["contentType"] = a.ContentType, ["bytes"] = a.Data.Length,
        })]),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
