using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravityHookCommand"/> (AI-1158): event-arg parsing and
/// the fail-open routing paths that must NOT touch the network (non-PreInvocation events,
/// malformed / incomplete payloads). The session-start POST path is exercised by the
/// WireMock integration suite.
/// </summary>
public class AntigravityHookCommandTests {
    [Test]
    public async Task EventArg_reads_the_positional_token_after_the_flag() {
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "PreInvocation"]))
            .IsEqualTo("PreInvocation");
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "Stop"]))
            .IsEqualTo("Stop");
    }

    [Test]
    public async Task EventArg_is_null_when_missing_or_a_flag_follows() {
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity"])).IsNull();
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "--debug"])).IsNull();
        await Assert.That(AntigravityHookCommand.EventArg(["hook"])).IsNull();
    }

    [Test]
    public async Task Missing_event_returns_error_without_touching_network() {
        var rc = await AntigravityHookCommand.Handle(
            "http://127.0.0.1:0", ["hook", "--antigravity"], new StringReader(""));
        await Assert.That(rc).IsEqualTo(1);
    }

    [Test]
    [Arguments("Stop")]
    [Arguments("PostInvocation")]
    [Arguments("PreToolUse")]
    [Arguments("PostToolUse")]
    public async Task Non_PreInvocation_events_are_no_ops(string ev) {
        // These must return 0 and never read stdin / hit the network.
        var rc = await AntigravityHookCommand.Handle(
            "http://127.0.0.1:0", ["hook", "--antigravity", ev],
            new ThrowingReader());
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    public async Task PreInvocation_with_malformed_payload_fails_open() {
        var rc = await AntigravityHookCommand.Handle(
            "http://127.0.0.1:0", ["hook", "--antigravity", "PreInvocation"],
            new StringReader("{ not json"));
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    public async Task PreInvocation_without_conversation_or_transcript_is_a_no_op() {
        // No conversationId → nothing to key on.
        await Assert.That(await AntigravityHookCommand.Handle(
            "http://127.0.0.1:0", ["hook", "--antigravity", "PreInvocation"],
            new StringReader("""{"transcriptPath":"/t.jsonl"}"""))).IsEqualTo(0);

        // conversationId but no transcriptPath → nothing to tail.
        await Assert.That(await AntigravityHookCommand.Handle(
            "http://127.0.0.1:0", ["hook", "--antigravity", "PreInvocation"],
            new StringReader("""{"conversationId":"abc"}"""))).IsEqualTo(0);
    }

    // A reader that throws if read — proves the non-PreInvocation path short-circuits
    // before consuming stdin.
    sealed class ThrowingReader : TextReader {
        public override Task<string> ReadToEndAsync() =>
            throw new InvalidOperationException("stdin must not be read for non-PreInvocation events");
    }
}
