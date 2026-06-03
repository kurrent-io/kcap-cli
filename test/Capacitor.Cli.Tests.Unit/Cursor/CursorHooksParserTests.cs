// test/Capacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Cursor;

namespace Capacitor.Cli.Tests.Unit.Cursor;

public class CursorHooksParserTests {
    [Test]
    public async Task EntryReferencesCapacitorCursorHook_true_for_bare_command() {
        var entry = JsonNode.Parse("""{"command":"kcap hook --cursor"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_true_for_command_with_extra_flags() {
        var entry = JsonNode.Parse("""{"command":"kcap hook --cursor --debug"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_false_for_third_party_command() {
        var entry = JsonNode.Parse("""{"command":"/usr/local/bin/other"}""");
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesCapacitorCursorHook_false_for_null() {
        await Assert.That(CursorHooksParser.EntryReferencesCapacitorCursorHook(null)).IsFalse();
    }

    [Test]
    public async Task HasCapacitorHooksFor_true_when_every_event_has_kcap_entry() {
        var root = JsonNode.Parse("""
            {"hooks": {
                "sessionStart": [{"command":"kcap hook --cursor"}],
                "sessionEnd":   [{"command":"kcap hook --cursor"}]
            }}
        """)!.AsObject();
        await Assert.That(CursorHooksParser.HasCapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsTrue();
    }

    [Test]
    public async Task HasCapacitorHooksFor_false_when_event_missing() {
        var root = JsonNode.Parse("""{"hooks": {"sessionStart": [{"command":"kcap hook --cursor"}]}}""")!.AsObject();
        await Assert.That(CursorHooksParser.HasCapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsFalse();
    }
}
