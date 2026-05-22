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
    public async Task Redacts_paths_with_trailing_separator_on_repoPath() {
        // repoPath ends with '/' — bubble JSON has the same path without trailing '/'.
        // Trailing-separator handling must make these equivalent for redaction.
        var raw = """{"bubbleId":"e1","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc","name":"edit_file_v2","params":"{\"relativeWorkspacePath\":\"/Users/me/code/foo/README.md\"}","result":"{}"}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: "/Users/me/code/foo/");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("/Users/me");
    }

    [Test]
    public async Task Redacts_backslash_paths_when_repoPath_uses_forward_slash() {
        // Windows scenario: workspace.json yields C:/Users/me/code/foo (forward slashes
        // via file:// URI), but bubble tool params encode native paths as
        // "C:\\Users\\me\\code\\foo\\X.cs" (one backslash escaped to two in JSON).
        // RedactPaths must match the backslash-escaped form even when repoPath is
        // given with forward slashes.
        var raw = """{"bubbleId":"e1","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc","name":"edit_file_v2","params":"{\"absPath\":\"C:\\\\Users\\\\me\\\\code\\\\foo\\\\X.cs\"}","result":"{}"}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: "C:/Users/me/code/foo");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("C:");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("Users");
    }

    [Test]
    public async Task Redacts_forward_slash_paths_when_repoPath_uses_backslash() {
        // Inverse Windows scenario: repoPath comes in with backslashes (post Path.GetFullPath
        // on Windows yields native form), but the bubble JSON has the path with forward
        // slashes (e.g., from an internal API that POSIX-ifies paths).
        var raw = """{"bubbleId":"e1","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc","name":"edit_file_v2","params":"{\"absPath\":\"C:/Users/me/code/foo/X.cs\"}","result":"{}"}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: @"C:\Users\me\code\foo");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("C:");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("Users");
    }

    [Test]
    public async Task Redacts_unescaped_backslash_paths_in_object_typed_params() {
        // When params is a JSON object (not a JSON-encoded string), GetRawText() returns
        // the literal JSON text where each backslash is encoded as "\\". The redaction
        // must still strip the leaked absolute path.
        var raw = """{"bubbleId":"e1","type":2,"capabilityType":15,"createdAt":"2026-05-21T00:00:00Z","toolFormerData":{"toolCallId":"tc","name":"edit_file_v2","params":{"absPath":"C:\\Users\\me\\code\\foo\\X.cs"},"result":{}}}""";
        var bubble = CursorPayloadAssembler.AssembleBubble(raw, repoPath: @"C:\Users\me\code\foo");
        await Assert.That(bubble.ToolFormerData!.Params).DoesNotContain("Users");
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
