using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class TryExtractUserTextTests {
    [Test]
    [Arguments("""{"type":"user","message":{"content":"hello world"}}""", "hello world")]
    [Arguments("""{"type":"user","message":{"content":"fix the bug"}}""", "fix the bug")]
    public async Task StringContent_ReturnsText(string line, string expected) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ArrayContent_ReturnsFirstTextElement() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"from array"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("from array");
    }

    [Test]
    public async Task ArrayContent_SkipsNonTextElements() {
        const string line   = """{"type":"user","message":{"content":[{"type":"image","url":"x"},{"type":"text","text":"second"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("second");
    }

    [Test]
    [Arguments("""{"type":"assistant","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"system","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"user","isMeta":true,"message":{"content":"meta stuff"}}""")]
    [Arguments("""{"type":"user","message":{"content":"<local-command-stdout>some output"}}""")]
    [Arguments("""{"type":"user","message":{"content":[{"type":"text","text":"<local-command-stdout>output"}]}}""")]
    [Arguments("not json at all")]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"type":"user"}""")]
    [Arguments("""{"type":"user","message":{}}""")]
    [Arguments("""{"type":"user","message":{"content":[]}}""")]
    public async Task ReturnsNull_ForInvalidOrFilteredInput(string line) {
        var result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }
}

public class StripSystemInstructionsTests {
    [Test]
    [Arguments("Hello <system_instructions>secret stuff</system_instructions> world", "Hello  world")]
    [Arguments("<system-instructions>block</system-instructions>actual prompt", "actual prompt")]
    [Arguments("<system-reminder>reminder content</system-reminder>do the thing", "do the thing")]
    [Arguments("<system_reminder>stuff</system_reminder>real text", "real text")]
    [Arguments("<SYSTEM_INSTRUCTIONS>loud</SYSTEM_INSTRUCTIONS>quiet", "quiet")]
    public async Task Strips_KnownSystemTags(string input, string expected) {
        var result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task PreservesText_WithNoSystemTags() {
        var result = WatchCommand.StripSystemInstructions("just a normal prompt");
        await Assert.That(result).IsEqualTo("just a normal prompt");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions() {
        var result = WatchCommand.StripSystemInstructions("<system_instructions>everything is instructions</system_instructions>");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ReturnsNull_ForNullInput() {
        var result = WatchCommand.StripSystemInstructions(null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_MultipleBlocks() {
        const string input  = "<system_instructions>first</system_instructions>middle<system-reminder>second</system-reminder>end";
        var          result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("middleend");
    }

    [Test]
    public async Task Strips_MultilineContent() {
        const string input  = "<system_instructions>\nline1\nline2\nline3\n</system_instructions>actual request";
        var          result = WatchCommand.StripSystemInstructions(input);
        await Assert.That(result).IsEqualTo("actual request");
    }

    [Test]
    public async Task CaseInsensitive_MixedCase() {
        var result = WatchCommand.StripSystemInstructions("<System_Instructions>stuff</System_Instructions>prompt");
        await Assert.That(result).IsEqualTo("prompt");
    }
}

public class TryExtractUserTextWithSystemInstructionsTests {
    [Test]
    public async Task Strips_SystemInstructions_FromStringContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>secret</system_instructions>fix the bug"}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    public async Task ReturnsNull_WhenOnlySystemInstructions_InContent() {
        const string line   = """{"type":"user","message":{"content":"<system_instructions>only instructions here</system_instructions>"}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Strips_SystemInstructions_FromArrayContent() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>reminder</system-reminder>do stuff"}]}}""";
        var          result = WatchCommand.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("do stuff");
    }
}

public class RepoPayloadChangedTests {
    static RepositoryPayload MakePayload(
            string owner    = "o",
            string repo     = "r",
            string branch   = "main",
            int?   prNumber = 1,
            string prUrl    = "u",
            string prTitle  = "t"
        ) => new() { Owner = owner, RepoName = repo, Branch = branch, PrNumber = prNumber, PrUrl = prUrl, PrTitle = prTitle };

    [Test]
    public async Task NullCurrent_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, MakePayload())).IsFalse();

    [Test]
    public async Task NullLastSent_ReturnsTrue() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), null)).IsTrue();

    [Test]
    public async Task BothNull_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, null)).IsFalse();

    [Test]
    public async Task SameValues_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), MakePayload())).IsFalse();

    [Test]
    [Arguments("Owner")]
    [Arguments("RepoName")]
    [Arguments("Branch")]
    [Arguments("PrNumber")]
    [Arguments("PrUrl")]
    [Arguments("PrTitle")]
    public async Task DifferentField_ReturnsTrue(string field) {
        var a = MakePayload();

        var b = field switch {
            "Owner"    => a with { Owner = "x" },
            "RepoName" => a with { RepoName = "x" },
            "Branch"   => a with { Branch = "x" },
            "PrNumber" => a with { PrNumber = 99 },
            "PrUrl"    => a with { PrUrl = "x" },
            "PrTitle"  => a with { PrTitle = "x" },
            _          => a
        };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsTrue();
    }

    [Test]
    public async Task NonComparedFields_DoNotTriggerChange() {
        var a = MakePayload() with { UserName = "alice" };
        var b = MakePayload() with { UserName = "bob" };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsFalse();
    }
}

public class CountFileLinesTests {
    [Test]
    [Arguments("line1\nline2\nline3\n", 3)]
    [Arguments("single", 1)]
    [Arguments("", 0)]
    public async Task CountsLines(string content, int expected) {
        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, content);
            await Assert.That(WatchCommand.CountFileLines(path)).IsEqualTo(expected);
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task MissingFile_ReturnsZero() =>
        await Assert.That(WatchCommand.CountFileLines("/tmp/nonexistent_" + Guid.NewGuid())).IsEqualTo(0);
}

public class WatchCommandTests {
    [Test]
    public async Task RunWatch_signature_accepts_vendor_arg() {
        // We can't run a real watcher in a unit test (it'd open SignalR). The
        // hook round-trip integration test exercises the wire path; this guards
        // the signature.
        var method      = typeof(WatchCommand).GetMethod(nameof(WatchCommand.RunWatch))!;
        var vendorParam = method.GetParameters().FirstOrDefault(p => p.Name == "vendor");
        await Assert.That(vendorParam).IsNotNull();
        await Assert.That(vendorParam!.HasDefaultValue).IsTrue();
        await Assert.That(vendorParam.DefaultValue).IsEqualTo("claude");
    }

    [Test]
    [Arguments(null, 60)]      // unset → default 60 min
    [Arguments("", 60)]        // empty → default
    [Arguments("abc", 60)]     // non-numeric → default
    [Arguments("0", 60)]       // non-positive → default (clamped)
    [Arguments("-5", 60)]      // negative → default
    [Arguments("15", 15)]      // valid override
    [Arguments("600", 600)]    // large but allowed
    public async Task ResolveCodexIdleTimeout_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveCodexIdleTimeout(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    static readonly DateTimeOffset IdleNow    = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan       IdleWindow = TimeSpan.FromMinutes(60);

    [Test]
    public async Task ShouldEndOnIdle_false_when_disconnected_time_covers_the_overage() {
        // 70 min of wall-clock since last activity, but 15 of those were a SignalR outage. Connected
        // idle = 55 min < 60 min window → must NOT idle-end (a mid-session outage is not idleness).
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(70), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(15));

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_true_when_connected_idle_exceeds_window_despite_prior_outage() {
        // 75 min wall-clock, 10 of them a brief outage → connected idle = 65 min > 60 min → idle-end.
        // (Models repeated reconnects with no new lines still ending after the connected budget.)
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(75), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false, disconnectedSinceActivity: TimeSpan.FromMinutes(10));

        await Assert.That(should).IsTrue();
    }

    [Test]
    public async Task ShouldEndOnIdle_default_disconnected_is_zero_preserving_prior_behavior() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow);

        await Assert.That(should).IsTrue();
    }

    [Test]
    public async Task ShouldEndOnIdle_true_for_idle_codex_session_watcher() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsTrue();
    }

    [Test]
    [Arguments("claude")]
    [Arguments("gemini")]
    [Arguments("pi")]
    [Arguments("copilot")]
    [Arguments("kiro")]
    public async Task ShouldEndOnIdle_false_for_non_codex(string vendor) {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: vendor, isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_not_yet_idle() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(59), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_for_subagent_watcher() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: false, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_below_threshold() {
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: false,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_exactly_at_timeout_boundary() {
        // Strictly greater-than: exactly == idleTimeout is NOT yet idle.
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(60), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: false);

        await Assert.That(should).IsFalse();
    }

    [Test]
    public async Task ShouldEndOnIdle_false_when_tool_in_flight_even_if_idle() {
        // A tool call is in progress — must NOT idle-end even after the timeout window.
        var should = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: IdleNow - TimeSpan.FromMinutes(61), now: IdleNow, idleTimeout: IdleWindow,
            toolInFlight: true);

        await Assert.That(should).IsFalse();
    }
}

public class UpdateCodexPendingToolCallsTests {
    [Test]
    public async Task FunctionCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{}"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsTrue();
    }

    [Test]
    public async Task CustomToolCall_AddsCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call","call_id":"call_2","name":"my_tool","arguments":"{}"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsTrue();
    }

    [Test]
    public async Task FunctionCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_1" };
        const string line = """{"type":"response_item","payload":{"type":"function_call_output","call_id":"call_1","output":"done"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_1")).IsFalse();
    }

    [Test]
    public async Task CustomToolCallOutput_RemovesCallId() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "call_2" };
        const string line = """{"type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"call_2","output":"done"}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        await Assert.That(pending.Contains("call_2")).IsFalse();
    }

    [Test]
    public async Task MessageResponseItem_LeavesSetUnchanged() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };
        const string line = """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[]}}""";

        WatchCommand.UpdateCodexPendingToolCalls(pending, line);

        // Set unchanged — still contains "existing", nothing added
        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }

    [Test]
    public async Task MalformedJson_LeavesSetUnchanged_NoThrow() {
        var pending = new HashSet<string>(StringComparer.Ordinal) { "existing" };

        // Must not throw; set must remain unchanged
        WatchCommand.UpdateCodexPendingToolCalls(pending, "not json at all {{}}");

        await Assert.That(pending.Count).IsEqualTo(1);
        await Assert.That(pending.Contains("existing")).IsTrue();
    }
}

public class CodexTranscriptExtractionTests {
    // Codex wraps every event in a response_item envelope; user prompts are
    // role:"user" message payloads with input_text blocks. See TitleGenerator
    // for the offline-import analog of this extraction.

    [Test]
    public async Task UserText_Extracts_InputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"user",
             "content":[{"type":"input_text","text":"fix the bug"}]}}
            """;

        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsEqualTo("fix the bug");
    }

    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task UserText_Skips_CodexInjectedPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"hi"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning","summary":[]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""")]
    [Arguments("""{"type":"user","message":{"content":"claude-shape"}}""")]
    [Arguments("not json")]
    public async Task UserText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = WatchCommand.TryExtractUserText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task AssistantText_Extracts_OutputText_FromResponseItem() {
        const string line = """
            {"type":"response_item","payload":{"type":"message","role":"assistant",
             "content":[{"type":"output_text","text":"Sure, let me look into that"}]}}
            """;

        var result = WatchCommand.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsEqualTo("Sure, let me look into that");
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"prompt"}]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"text","text":"claude-shape"}]}}""")]
    public async Task AssistantText_ReturnsNull_ForUnrelatedCodexLines(string line) {
        var result = WatchCommand.TryExtractAssistantText(line, "codex");

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"response_item","payload":{"type":"reasoning"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"function_call"}}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"user","content":[]}}""", false)]
    public async Task IsEvent_Codex_OnlyCountsMessagePayloads(string line, bool expected) {
        var result = WatchCommand.IsEvent(line, "codex");

        await Assert.That(result).IsEqualTo(expected);
    }

    // Critical: prelude user-role payloads must NOT count toward the 5-event
    // title threshold. Otherwise a fresh Codex session with a few injected
    // <environment_context>/AGENTS.md entries before any real prompt can trip
    // the threshold and produce a title from prelude content alone.
    [Test]
    [Arguments("<environment_context>\nworkspace=/tmp\n</environment_context>")]
    [Arguments("# AGENTS.md instructions for /tmp\n\nUse pnpm.")]
    [Arguments("<turn_aborted>user pressed esc</turn_aborted>")]
    public async Task IsEvent_Codex_SkipsInjectedUserPreludes(string preludeText) {
        var encoded = System.Text.Json.JsonSerializer.Serialize(preludeText);
        var line    = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":" + encoded + "}]}}";

        var result = WatchCommand.IsEvent(line, "codex");

        await Assert.That(result).IsFalse();
    }
}

// AI-886 / PR #162: Pi emits type:"message" with message.role (not Claude's
// top-level user/assistant), so the watcher title helpers need a Pi branch —
// otherwise live Pi sessions never get the initial/LLM title.
public class PiTitleHelperTests {
    [Test]
    public async Task UserText_StringContent() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":"build the thing"}}""";
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsEqualTo("build the thing");
    }

    [Test]
    public async Task UserText_ArrayContent_FirstTextBlock_ImagesSkipped() {
        const string line = """{"type":"message","id":"a1","message":{"role":"user","content":[{"type":"image","data":"x"},{"type":"text","text":"look at this"}]}}""";
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsEqualTo("look at this");
    }

    [Test]
    public async Task AssistantText_FirstTextBlock_ThinkingSkipped() {
        const string line = """{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"on it"}]}}""";
        await Assert.That(WatchCommand.TryExtractAssistantText(line, "pi")).IsEqualTo("on it");
    }

    [Test]
    [Arguments("""{"type":"message","id":"a1","message":{"role":"user","content":"hi"}}""", true)]
    [Arguments("""{"type":"message","id":"b1","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""", false)]
    [Arguments("""{"type":"session","id":"11111111-2222-3333-4444-555555555555","cwd":"/w"}""", false)]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""", false)]
    [Arguments("""{"type":"user","message":{"content":"claude shape"}}""", false)]
    // Empty/contentless Pi user & assistant envelopes must NOT count toward the
    // title-event threshold — they produce no canonical event (mirrors the server
    // normalizer / PiImportSource.IsImportRelevantLine).
    [Arguments("""{"type":"message","id":"e1","message":{"role":"user","content":""}}""", false)]
    [Arguments("""{"type":"message","id":"e2","message":{"role":"user","content":[]}}""", false)]
    [Arguments("""{"type":"message","id":"e3","message":{"role":"user","content":[{"type":"image","data":"x"}]}}""", false)]
    [Arguments("""{"type":"message","id":"e4","message":{"role":"assistant","content":[]}}""", false)]
    // Tool-only assistant turns DO count (NormalizeAssistant emits a tool-call event).
    [Arguments("""{"type":"message","id":"e5","message":{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"bash"}]}}""", true)]
    public async Task IsEvent_Pi_OnlyCountsMessageUserOrAssistant(string line, bool expected) {
        await Assert.That(WatchCommand.IsEvent(line, "pi")).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"type":"model_change","id":"d1","modelId":"gpt-5"}""")]
    [Arguments("""{"type":"message","id":"c1","message":{"role":"toolResult","toolCallId":"t1","content":[]}}""")]
    public async Task TitleHelpers_ReturnNull_ForNonConversationalPiLines(string line) {
        await Assert.That(WatchCommand.TryExtractUserText(line, "pi")).IsNull();
        await Assert.That(WatchCommand.TryExtractAssistantText(line, "pi")).IsNull();
    }
}
