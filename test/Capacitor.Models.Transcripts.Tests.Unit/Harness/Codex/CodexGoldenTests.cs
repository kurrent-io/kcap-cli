using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Codex;
using Google.Protobuf;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

/// <summary>Whole synthetic rollouts projected through one context, every channel of every result
/// compared against a reviewed golden file. A `#batch` line is a batch boundary: the context's
/// BeginBatch runs there and the line itself is not projected or numbered. Run with
/// <c>KCAP_UPDATE_GOLDEN=1</c> to rewrite the goldens after a deliberate change, then review the diff.</summary>
public class CodexGoldenTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    const string Boundary = "#batch";

    [Test]
    [Arguments("codex-parity")]
    [Arguments("codex-subagent")]
    public async Task The_fixture_projects_to_its_golden_file(string fixture) {
        var dir       = Path.Combine(SourceDir(), "Fixtures");
        var lines     = await File.ReadAllLinesAsync(Path.Combine(dir, fixture + ".jsonl"));
        var ctx       = CodexRolloutEvents.Instance.CreateContext("sess", null);
        var projected = new JsonArray();
        var batch     = 1;
        var number    = 0;

        ctx.BeginBatch();
        foreach (var line in lines) {
            if (line == Boundary) {
                ctx.BeginBatch();
                batch++;
                continue;
            }
            var result = CodexRolloutEvents.Instance.Project(line, ++number, Received, ctx);
            projected.Add(new JsonObject {
                ["line"]       = number,
                ["batch"]      = batch,
                ["rejected"]   = result.Rejected is null ? null : JsonValue.Create(true),
                ["events"]     = new JsonArray([.. result.Events.Select(Render)]),
                ["amendments"] = new JsonArray([.. result.Amendments.Select(a => (JsonNode)new JsonObject {
                    ["target"]    = a.TargetEventId.ToString(),
                    ["slug"]      = a.Slug,
                    ["extension"] = JsonNode.Parse(JsonFormatter.Default.Format(a.Extension)),
                })]),
            });
        }

        var actual   = projected.ToJsonString(Indented).Replace("\r\n", "\n");
        var expected = Path.Combine(dir, fixture + ".expected.json");
        if (Environment.GetEnvironmentVariable("KCAP_UPDATE_GOLDEN") == "1") await File.WriteAllTextAsync(expected, actual + "\n");

        await Assert.That(actual).IsEqualTo((await File.ReadAllTextAsync(expected)).Replace("\r\n", "\n").TrimEnd('\n'));
    }

    static JsonNode Render(CanonicalEvent e) => new JsonObject {
        ["type"]            = e.EventType,
        ["id"]              = e.EventId.ToString(),
        ["timestamp"]       = e.Timestamp.ToString("O"),
        ["recordTimestamp"] = e.RecordTimestamp,
        ["causedBy"]        = e.CausedBy,
        ["payload"]         = Payload(e.Payload),
        ["usage"]           = e.Usage is null ? null : JsonNode.Parse(JsonFormatter.Default.Format(e.Usage)),
        ["usageIsEcho"]     = e.UsageIsEcho,
    };

    static JsonNode? Payload(object payload) => payload switch {
        IMessage message       => JsonNode.Parse(JsonFormatter.Default.Format(message)),
        UsageApplied applied   => new JsonObject {
            ["usage"]   = JsonNode.Parse(JsonFormatter.Default.Format(applied.Usage)),
            ["anchor"]  = applied.AnchorEventId.ToString(),
            ["targets"] = new JsonArray([.. applied.Targets.Select(t => (JsonNode)new JsonObject {
                ["eventId"] = t.EventId.ToString(), ["eventType"] = t.EventType, ["toolName"] = t.ToolName, ["isEcho"] = t.IsEcho,
            })]),
        },
        ContextCompactedEvent compacted => JsonNode.Parse(JsonSerializer.Serialize(compacted, CodexJsonContext.Default.ContextCompactedEvent)),
        _ => throw new InvalidOperationException($"No rendering for {payload.GetType().Name}"),
    };

    static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
