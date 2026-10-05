using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudePlanReadTests {
    static string Payload(string tool = "Read", string path = "/repo/docs/plans/x.md", string? content = "# Plan",
                          string ev = "PostToolUse") {
        var file = new JsonObject { ["filePath"] = path };
        if (content is not null) file["content"] = content;
        return new JsonObject {
            ["hook_event_name"] = ev, ["session_id"] = "aaaa-bbbb", ["tool_name"] = tool,
            ["tool_input"]      = new JsonObject { ["file_path"] = path },
            ["tool_response"]   = new JsonObject { ["type"] = "text", ["file"] = file },
        }.ToJsonString();
    }

    [Test]
    public async Task Parses_a_markdown_read_with_a_dashless_session_id() {
        var read = ClaudePlanRead.Parse(Payload())!;

        await Assert.That(read.SessionId).IsEqualTo("aaaabbbb");
        await Assert.That(read.Path).IsEqualTo("/repo/docs/plans/x.md");
        await Assert.That(read.Content).IsEqualTo("# Plan");
    }

    [Test]
    [Arguments("Edit", "/repo/docs/plans/x.md", "PostToolUse")]
    [Arguments("Read", "/repo/docs/plans/x.txt", "PostToolUse")]
    [Arguments("Read", "/repo/docs/plans/x.md", "PreToolUse")]
    public async Task Ignores_anything_but_a_markdown_read_after_the_tool_ran(string tool, string path, string ev) {
        await Assert.That(ClaudePlanRead.Parse(Payload(tool, path, ev: ev))).IsNull();
    }

    [Test]
    public async Task Ignores_a_body_that_is_not_json() {
        await Assert.That(ClaudePlanRead.Parse("not json")).IsNull();
    }

    [Test]
    public async Task Omits_content_from_the_request_when_the_response_carries_none() {
        var request = ClaudePlanRead.Parse(Payload(content: null))!.ToRequest();

        await Assert.That(request.ContainsKey("content")).IsFalse();
    }

    [Test]
    public async Task Sends_only_the_head_of_a_long_document() {
        var read = ClaudePlanRead.Parse(Payload(content: new string('x', ClaudePlanRead.MaxContentChars * 2)))!;

        await Assert.That(read.Content!.Length).IsEqualTo(ClaudePlanRead.MaxContentChars);
    }

    [Test]
    public async Task Never_splits_a_surrogate_pair_at_the_cut() {
        var content = new string('x', ClaudePlanRead.MaxContentChars - 1) + "😀" + "tail";

        var read = ClaudePlanRead.Parse(Payload(content: content))!;

        await Assert.That(read.Content!.Length).IsEqualTo(ClaudePlanRead.MaxContentChars - 1);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"nudge":"  "}""")]
    [Arguments("""{"nudge":42}""")]
    [Arguments("not json")]
    public async Task Reads_no_nudge_from_an_answer_without_text(string body) {
        await Assert.That(ClaudePlanRead.ReadNudge(body)).IsNull();
    }
}
