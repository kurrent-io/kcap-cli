using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

public class CodexHooksParserTests {
    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_true_when_command_contains_marker() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kapacitor codex-hook","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_when_command_does_not_match() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"echo hi","timeout":30}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesKapacitorCodexHook_returns_false_for_null() {
        await Assert.That(CodexHooksParser.EntryReferencesKapacitorCodexHook(null)).IsFalse();
    }

    [Test]
    public async Task HasKapacitorHooksFor_returns_true_when_all_events_have_kapacitor_entry() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasKapacitorHooksFor(root!, events)).IsTrue();
    }

    [Test]
    public async Task HasKapacitorHooksFor_returns_false_when_one_event_missing() {
        var root = JsonNode.Parse("""
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kapacitor codex-hook"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"echo something else"}]}]
            }}
        """) as JsonObject;
        var events = new[] { "SessionStart", "Stop", "PermissionRequest" };
        await Assert.That(CodexHooksParser.HasKapacitorHooksFor(root!, events)).IsFalse();
    }

    [Test]
    public async Task CodexHookEvents_lists_all_six_events_in_canonical_order() {
        var expected = new[] {
            "SessionStart", "UserPromptSubmit", "PreToolUse",
            "PostToolUse", "PermissionRequest", "Stop"
        };

        await Assert.That(CodexHooksParser.CodexHookEvents.Length).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++) {
            await Assert.That(CodexHooksParser.CodexHookEvents[i]).IsEqualTo(expected[i]);
        }
    }
}
