using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A live session watcher holds its lines back until they contain a real turn or reach the threshold.
/// A short session with a turn is sent and ended; a session nobody took a turn in sends no lines but
/// is still ended.
/// </summary>
public class ShortLiveSessionTests {
    const string PiHeader        = """{"type":"session","version":3,"id":"0f9c3a52-3f1e-4b8a-9a77-6b1e2f0c4d11","cwd":"/w"}""";
    const string PiModelChange   = """{"type":"model_change","provider":"anthropic","modelId":"m"}""";
    const string PiThinking      = """{"type":"thinking_level_change","thinkingLevel":"off"}""";
    const string PiPrompt        = """{"type":"message","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}""";
    const string OpenCodePrompt  = """{"info":{"role":"user"},"parts":[{"type":"text","text":"fix the bug"}]}""";
    const string OpenCodeReply   = """{"info":{"role":"assistant"},"parts":[{"type":"text","text":"Fixed."}]}""";
    const string OpenCodeToolRun = """{"info":{"role":"assistant"},"parts":[{"type":"tool","tool":"read"}]}""";

    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new FixedCredentialSource(), TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()), new GitProviderRouter(), TimeProvider.System, TestAccounts.None, Home);

    static readonly string Sid = Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() => new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    async Task<IReadOnlyList<string>> Drain(string path, string vendor, WatchState state) {
        await using var hub = UnconnectedHub();
        return await Watch.DrainNewLines(hub, Sid, path, agentId: null, state, vendor, CancellationToken.None);
    }

    [Test]
    [Arguments("opencode", OpenCodePrompt, true)]
    [Arguments("opencode", OpenCodeReply, true)]
    [Arguments("opencode", OpenCodeToolRun, false)]
    [Arguments("pi", PiPrompt, true)]
    [Arguments("pi", PiHeader, false)]
    [Arguments("pi", PiModelChange, false)]
    [Arguments("claude", """{"type":"user","message":{"role":"user","content":"Explain this"}}""", true)]
    [Arguments("claude", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Sure"}]}}""", true)]
    [Arguments("claude", """{"type":"user","isMeta":true,"message":{"role":"user","content":"<local-command-caveat>Caveat</local-command-caveat>"}}""", false)]
    [Arguments("claude", """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>"}}""", false)]
    [Arguments("claude", """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model</local-command-stdout>"}}""", false)]
    [Arguments("claude", """{"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]}}""", true)]
    [Arguments("claude", """{"type":"user","isMeta":true,"message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]}}""", false)]
    [Arguments("claude", """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""", false)]
    [Arguments("codex", """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"fix it"}]}}""", true)]
    [Arguments("codex", """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context><cwd>/w</cwd></environment_context>"}]}}""", false)]
    [Arguments("codex", """{"type":"session_meta","payload":{"id":"x"}}""", false)]
    [Arguments("gemini", """{"type":"user","content":[{"text":"hello"}]}""", true)]
    [Arguments("gemini", """{"type":"user","content":"<session_context>ctx</session_context>"}""", false)]
    public async Task A_real_turn_is_a_prompt_or_a_reply(string vendor, string line, bool expected) {
        await Assert.That(WatchCommand.IsRealTurnLine(line, vendor, TimeProvider.System)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_buffer_with_a_turn_flushes_below_the_threshold() {
        await Assert.That(WatchCommand.ShouldFlushBuffer([PiHeader, PiPrompt], "pi", TimeProvider.System)).IsTrue();
    }

    [Test]
    public async Task A_turnless_buffer_flushes_only_once_it_reaches_the_threshold() {
        var below = Enumerable.Repeat(PiModelChange, WatchState.TranscriptThreshold - 1).ToList();
        var at    = Enumerable.Repeat(PiModelChange, WatchState.TranscriptThreshold).ToList();

        await Assert.That(WatchCommand.ShouldFlushBuffer(below, "pi", TimeProvider.System)).IsFalse();
        await Assert.That(WatchCommand.ShouldFlushBuffer(at, "pi", TimeProvider.System)).IsTrue();
    }

    /// <summary>The shape of the reported OpenCode run: a whole turn in fewer lines than the threshold.</summary>
    [Test]
    public async Task A_short_session_with_a_turn_streams_on_its_first_poll() {
        var path  = Tmp.CreateFile("t.jsonl", Lines(OpenCodePrompt, OpenCodeToolRun, OpenCodeReply));
        var state = new WatchState();

        var sent = await Drain(path, "opencode", state);

        await Assert.That(state.ThresholdReached).IsTrue();
        await Assert.That(state.BufferedLines).IsEmpty();
        await Assert.That(sent.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_turnless_session_keeps_its_lines_back() {
        var path  = Tmp.CreateFile("t.jsonl", Lines(PiHeader, PiModelChange, PiThinking));
        var state = new WatchState();

        await Drain(path, "pi", state);

        await Assert.That(state.ThresholdReached).IsFalse();
        await Assert.That(state.BufferedLines.Count).IsEqualTo(3);
    }

    /// <summary>Every poll re-reads the unsent lines, so appending them would count each one again and
    /// carry copies across the threshold.</summary>
    [Test]
    public async Task Polling_a_turnless_session_again_does_not_buffer_its_lines_twice() {
        var lines = Enumerable.Repeat(PiModelChange, WatchState.TranscriptThreshold - 1).ToArray();
        var path  = Tmp.CreateFile("t.jsonl", Lines(lines));
        var state = new WatchState();

        await Drain(path, "pi", state);
        await Drain(path, "pi", state);

        await Assert.That(state.ThresholdReached).IsFalse();
        await Assert.That(state.BufferedLines.Count).IsEqualTo(lines.Length);
        await Assert.That(state.BufferedLineNumbers).IsEquivalentTo(Enumerable.Range(0, lines.Length).ToArray());
    }

    [Test]
    public async Task A_turnless_session_crossing_the_threshold_flushes_each_line_once() {
        var path  = Tmp.CreateFile("t.jsonl", Lines(PiHeader, PiModelChange, PiThinking));
        var state = new WatchState();
        await Drain(path, "pi", state);

        await File.AppendAllTextAsync(path, Lines(Enumerable.Repeat(PiModelChange, WatchState.TranscriptThreshold - 3).ToArray()));
        var sent = await Drain(path, "pi", state);

        await Assert.That(state.ThresholdReached).IsTrue();
        await Assert.That(state.BufferedLines).IsEmpty();
        await Assert.That(sent.Count).IsEqualTo(WatchState.TranscriptThreshold);
    }

    [Test]
    public async Task A_turn_written_after_the_last_poll_is_found_at_shutdown() {
        var path = Tmp.CreateFile("t.jsonl", Lines(PiHeader, PiModelChange, PiPrompt));

        await Assert.That(WatchCommand.UnsentLinesHoldRealTurn(path, 0, "pi", TimeProvider.System)).IsTrue();
    }

    [Test]
    public async Task A_turnless_transcript_holds_no_turn_at_shutdown() {
        var path = Tmp.CreateFile("t.jsonl", Lines(PiHeader, PiModelChange, PiThinking));

        await Assert.That(WatchCommand.UnsentLinesHoldRealTurn(path, 0, "pi", TimeProvider.System)).IsFalse();
    }

    /// <summary>Pi writes no session file until the first turn, so a session that ended before one has none.</summary>
    [Test]
    public async Task A_transcript_that_was_never_written_holds_no_turn() {
        await Assert.That(WatchCommand.UnsentLinesHoldRealTurn(Tmp.PathTo("missing.jsonl"), 0, "pi", TimeProvider.System)).IsFalse();
    }

    [Test]
    public async Task A_turn_the_server_already_holds_is_not_counted_again() {
        var path = Tmp.CreateFile("t.jsonl", Lines(PiPrompt, PiModelChange));

        await Assert.That(WatchCommand.UnsentLinesHoldRealTurn(path, 1, "pi", TimeProvider.System)).IsFalse();
    }

    [Test]
    [Arguments("parent_exited", null, false, true)]
    [Arguments("idle_timeout", null, false, true)]
    [Arguments("parent_exited", "agent-1", false, false)]
    [Arguments(null, null, false, false)]
    [Arguments("idle_timeout", null, true, false)]
    public async Task The_watcher_ends_its_session_whatever_the_session_holds(string? endReason, string? agentId, bool cursorSuppresses, bool expected) {
        await Assert.That(WatchCommand.PostsOwnSessionEnd(endReason, agentId, cursorSuppresses)).IsEqualTo(expected);
    }
}
