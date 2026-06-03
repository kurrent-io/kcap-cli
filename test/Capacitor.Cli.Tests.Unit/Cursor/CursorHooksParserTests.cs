// test/Capacitor.Cli.Tests.Unit/Cursor/CursorHooksParserTests.cs
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Cursor;

namespace Capacitor.Cli.Tests.Unit.Cursor;

public class CursorHooksParserTests {
    [Test]
    public async Task EntryReferencesKapacitorCursorHook_true_for_bare_command() {
        var entry = JsonNode.Parse("""{"command":"kapacitor hook --cursor"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_true_for_command_with_extra_flags() {
        var entry = JsonNode.Parse("""{"command":"kapacitor hook --cursor --debug"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_false_for_third_party_command() {
        var entry = JsonNode.Parse("""{"command":"/usr/local/bin/other"}""");
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCursorHook_false_for_null() {
        await Assert.That(CursorHooksParser.EntryReferencesKapacitorCursorHook(null)).IsFalse();
    }

    [Test]
    public async Task HasKapacitorHooksFor_true_when_every_event_has_kapacitor_entry() {
        var root = JsonNode.Parse("""
            {"hooks": {
                "sessionStart": [{"command":"kapacitor hook --cursor"}],
                "sessionEnd":   [{"command":"kapacitor hook --cursor"}]
            }}
        """)!.AsObject();
        await Assert.That(CursorHooksParser.HasKapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsTrue();
    }

    [Test]
    public async Task HasKapacitorHooksFor_false_when_event_missing() {
        var root = JsonNode.Parse("""{"hooks": {"sessionStart": [{"command":"kapacitor hook --cursor"}]}}""")!.AsObject();
        await Assert.That(CursorHooksParser.HasKapacitorHooksFor(root, ["sessionStart", "sessionEnd"]))
            .IsFalse();
    }
}
