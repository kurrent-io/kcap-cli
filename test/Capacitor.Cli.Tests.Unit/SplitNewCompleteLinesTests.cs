using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

// AI-1243 ("endless reads in chat"): the live watcher drain must NOT consume a transcript
// line that is not yet newline-terminated — the agent is mid-write of it. Sending the
// truncated prefix and advancing the position past it permanently drops the completed line
// (its truncated JSON fails to normalize server-side, and the next drain starts after it).
// This disproportionately hit large `Read` tool_result lines (long JSON, slower to flush),
// which then rendered as orphaned tool calls. The guard holds the partial line back until a
// later drain sees it newline-terminated.
public class SplitNewCompleteLinesTests {
    [Test]
    public async Task All_lines_returned_when_file_ends_with_newline() {
        var r = WatchCommand.SplitNewCompleteLines("a\nb\nc\n", 0);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 1, 2 });
        await Assert.That(r.NextPosition).IsEqualTo(3);
    }

    [Test]
    public async Task Partial_final_line_is_held_back() {
        // "c" has no trailing newline — still being written.
        var r = WatchCommand.SplitNewCompleteLines("a\nb\nc", 0);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 1 });
        // Position stays BEFORE the partial line so the next drain re-reads it complete.
        await Assert.That(r.NextPosition).IsEqualTo(2);
    }

    [Test]
    public async Task Dropped_read_result_is_delivered_once_complete() {
        // Reproduces AI-1243: a tool_use line (complete) followed by its tool_result line
        // caught mid-write (no trailing newline yet).
        var first = WatchCommand.SplitNewCompleteLines("tool_use_A\ntool_result_A", 0);

        // Only the complete tool_use is sent; the partial result is held back — NOT dropped.
        await Assert.That(first.Lines).IsEquivalentTo(new[] { "tool_use_A" });
        await Assert.That(first.NextPosition).IsEqualTo(1);

        // Next drain, after the writer finished the result line and appended the next tool_use.
        var second = WatchCommand.SplitNewCompleteLines("tool_use_A\ntool_result_A\ntool_use_B\n", first.NextPosition);

        await Assert.That(second.Lines).IsEquivalentTo(new[] { "tool_result_A", "tool_use_B" });
        await Assert.That(second.LineNumbers).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(second.NextPosition).IsEqualTo(3);
    }

    [Test]
    public async Task Respects_lines_already_processed() {
        var r = WatchCommand.SplitNewCompleteLines("a\nb\nc\n", 2);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "c" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 2 });
        await Assert.That(r.NextPosition).IsEqualTo(3);
    }

    [Test]
    public async Task Blank_lines_skipped_from_output_but_counted_in_position() {
        var r = WatchCommand.SplitNewCompleteLines("a\n\nb\n", 0);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 2 });
        await Assert.That(r.NextPosition).IsEqualTo(3);
    }

    [Test]
    public async Task Crlf_terminated_lines_are_complete() {
        var r = WatchCommand.SplitNewCompleteLines("a\r\nb\r\n", 0);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(r.NextPosition).IsEqualTo(2);
    }

    [Test]
    public async Task Empty_file_yields_nothing() {
        var r = WatchCommand.SplitNewCompleteLines("", 0);

        await Assert.That(r.Lines).IsEmpty();
        await Assert.That(r.NextPosition).IsEqualTo(0);
    }

    [Test]
    public async Task Final_drain_flushes_the_incomplete_final_line() {
        // At session end the file is static — an unterminated final line is genuinely the end, so
        // the final drain must deliver it (a vendor that never newline-terminates its last line
        // must not lose it). Only the LIVE polling drain holds partial lines back.
        var r = WatchCommand.SplitNewCompleteLines("a\nb\nc", 0, holdIncompleteFinalLine: false);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0, 1, 2 });
        await Assert.That(r.NextPosition).IsEqualTo(3);
    }

    [Test]
    public async Task A_lone_partial_line_holds_position_at_start() {
        var r = WatchCommand.SplitNewCompleteLines("partial-json-being-written", 0);

        await Assert.That(r.Lines).IsEmpty();
        await Assert.That(r.NextPosition).IsEqualTo(0);
    }

    [Test]
    public async Task Blank_partial_final_line_held_back_without_dropping_prior_content() {
        // A whitespace-only final line lacking a newline must not advance the position past
        // itself (it may still be completing), but the real line before it is still delivered.
        var r = WatchCommand.SplitNewCompleteLines("real\n   ", 0);

        await Assert.That(r.Lines).IsEquivalentTo(new[] { "real" });
        await Assert.That(r.LineNumbers).IsEquivalentTo(new[] { 0 });
        await Assert.That(r.NextPosition).IsEqualTo(1);
    }
}
