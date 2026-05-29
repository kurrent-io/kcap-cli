using System.Text.Json;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorPayloadModelsTests {
    [Test]
    public async Task cli_owner_and_cli_repo_serialize_when_set() {
        var p = new CursorImportPayload {
            Vendor              = "cursor",
            ComposerId          = "abc",
            CliOwner            = "eventstore",
            CliRepo             = "kapacitor-server",
            SchemaSourceVersion = new() { ComposerData = 1, Bubble = 1 },
            Header              = new() { UnifiedMode = "agent", CreatedAtMs = 0, LastUpdatedAtMs = 0 },
            ComposerData        = MinimalComposerData(),
            Bubbles             = [],
            ContentBlobs        = new Dictionary<string, string>(),
        };
        var json = JsonSerializer.Serialize(p);
        await Assert.That(json).Contains("\"cli_owner\":\"eventstore\"");
        await Assert.That(json).Contains("\"cli_repo\":\"kapacitor-server\"");
    }

    [Test]
    public async Task cli_fields_omitted_when_null() {
        var p = new CursorImportPayload {
            Vendor              = "cursor",
            ComposerId          = "abc",
            SchemaSourceVersion = new() { ComposerData = 1, Bubble = 1 },
            Header              = new() { UnifiedMode = "agent", CreatedAtMs = 0, LastUpdatedAtMs = 0 },
            ComposerData        = MinimalComposerData(),
            Bubbles             = [],
            ContentBlobs        = new Dictionary<string, string>(),
        };
        var json = JsonSerializer.Serialize(p);
        await Assert.That(json).DoesNotContain("cli_owner");
        await Assert.That(json).DoesNotContain("cli_repo");
    }

    static CursorComposerData MinimalComposerData() => new() {
        ModelConfig                 = new() { ModelName = "default" },
        FullConversationHeadersOnly = [],
        GeneratingBubbleIds         = []
    };
}
