using Kapacitor.Cli.Core.Cursor;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorPayloadAssemblerTests {
    [Test]
    public async Task Drops_disallowed_bubble_fields() {
        var raw = """{"bubbleId":"b1","type":1,"createdAt":"2026-05-21T00:00:00Z","text":"hi","richText":"DROP","images":["DROP"],"attachedCodeChunks":["DROP"]}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: "/repo/foo");
        await Assert.That(bubble.Text).IsEqualTo("hi");
        var json = System.Text.Json.JsonSerializer.Serialize(bubble);
        await Assert.That(json).DoesNotContain("DROP");
    }

    [Test]
    public async Task Redacts_absolute_paths_to_relative() {
        var raw = """{"bubbleId":"e1","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc","name":"edit_file_v2","params":"{\"relativeWorkspacePath\":\"/Users/me/code/foo/README.md\"}","result":"{}"}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: "/Users/me/code/foo");
        await Assert.That(bubble.ToolFormerData!.Params).Contains("/README.md");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("/Users/me");
    }

    [Test]
    public async Task Handles_object_typed_params_and_result() {
        // Real Cursor data can have params/result as JSON objects (not JSON-encoded strings)
        var raw = """{"bubbleId":"b2","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc2","name":"edit_file_v2","params":{"relativeWorkspacePath":"src/foo.cs"},"result":{"success":true}}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: "/repo/foo");
        await Assert.That(bubble.ToolFormerData).IsNotNull();
        // Params/result should be serialized back to JSON text, not throw
        await Assert.That(bubble.ToolFormerData!.Params).Contains("relativeWorkspacePath");
        await Assert.That(bubble.ToolFormerData!.Result).Contains("success");
    }

    [Test]
    public async Task Truncates_oversize_content_blob() {
        var big = new string('x', 300_000);
        var (key, value) = CursorPayloadAssembler.MaybeTruncateBlob("composer.content.aaa", big);
        await Assert.That(value).Contains("\"truncated\":true");
        await Assert.That(value.Length).IsLessThan(1_000);
    }
}
