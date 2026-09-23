using Capacitor.App.Services;
using SvcSystems.UI.Terminal;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

// Feed(byte[]) re-decodes every call, so PTY bytes go through one Utf8StreamDecoder and then
// Feed(string). Terminal replies (DSR/CPR) arrive only on Terminal.Engine.DataReceived, not on
// the model's UserInput.

/// The acceptance gate for emulation fidelity: a captured ANSI/TUI stream
/// (colors, cursor addressing, alternate screen) through the decode-and-feed
/// path, ReflowOnResize=false per upstream's own TUI guidance.
[NotInParallel("AvaloniaSession")]
public class TerminalTranscriptTests {
    [Test]
    public async Task A_recorded_tui_transcript_feeds_without_faulting_and_lands_expected_cells() {
        var (row0Text, row0Bold, row0FgColor, midCells, alternateActiveDuringAltScreen,
                isAlternateBufferActiveAfter, mainBufferText) =
            await AvaloniaSession.DispatchAsync(() => {
                var model = new TerminalControlModel(new TerminalOptions {
                    Cols = 80,
                    Rows = 24,
                    ReflowOnResize = false,
                });
                var decoder = new Utf8StreamDecoder();
                var engine = model.Terminal.Engine;

                // transcript, split at the alt-screen boundary so ENTRY can be pinned (not just
                // exit): prefix ends with \x1b[?1049h; suffix carries the alt-only text, the
                // \x1b[?1049l exit, and the post-exit text.
                var prefix = "\x1b[2J\x1b[H\x1b[1;31mRED\x1b[0m\x1b[10;5Hmid\x1b[?1049h"u8.ToArray();
                var suffix = " alt \x1b[?1049l back"u8.ToArray();

                foreach (var chunk in Chunk(prefix, 7)) // deliberately ugly boundaries
                    model.Feed(decoder.Decode(chunk));
                var alternateActiveDuringAltScreen = engine.IsAlternateBufferActive;

                foreach (var chunk in Chunk(suffix, 7)) // deliberately ugly boundaries
                    model.Feed(decoder.Decode(chunk));
                model.Feed(decoder.Flush());

                var row0 = engine.Buffer.Lines[0]!;
                var row9 = engine.Buffer.Lines[9]!;
                var mid = new[] { row9[4].Content, row9[5].Content, row9[6].Content };

                var mainText = string.Join('\n', Enumerable.Range(0, engine.Rows).Select(engine.GetLine));

                return (
                    row0Text: engine.GetLine(0),
                    row0Bold: row0[0].Attributes.IsBold(),
                    row0FgColor: row0[0].Attributes.GetFgColor(),
                    midCells: mid,
                    alternateActiveDuringAltScreen,
                    isAlternateBufferActiveAfter: engine.IsAlternateBufferActive,
                    mainBufferText: mainText);
            });

        await Assert.That(row0Text).IsEqualTo("RED");
        await Assert.That(row0Bold).IsTrue();
        await Assert.That(row0FgColor).IsEqualTo(1); // SGR 31 -> legacy palette index 1 (red)
        await Assert.That(midCells).IsEquivalentTo(["m", "i", "d"], CollectionOrdering.Matching);
        await Assert.That(alternateActiveDuringAltScreen).IsTrue();  // alt-screen ENTRY pinned
        await Assert.That(isAlternateBufferActiveAfter).IsFalse();   // alt-screen EXIT pinned
        await Assert.That(mainBufferText).Contains("back");
        // The alt-buffer's own text must NOT leak into the main buffer once switched back;
        // without this the test passes whether \x1b[?1049h/l are interpreted or no-op'd.
        await Assert.That(mainBufferText).DoesNotContain("alt");
    }

    static IEnumerable<byte[]> Chunk(byte[] data, int size) {
        for (var i = 0; i < data.Length; i += size) yield return data[i..Math.Min(i + size, data.Length)];
    }

    // ── XtermTerminalSurface wiring ─────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_device_status_query_produces_a_terminal_reply_through_InputProduced() {
        // The engine answers a DSR (Device Status Report / cursor-position query) on its own,
        // via Terminal.Engine.DataReceived -- reachable only through XtermTerminalSurface
        // subscribing on the raw engine object, not the wrapper or model.
        var replyContainsCursorPositionReport = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            var replies = new List<byte[]>();
            surface.InputProduced += replies.Add;

            surface.Feed("\x1b[6n"); // DSR: report cursor position

            return replies.Any(r => System.Text.Encoding.ASCII.GetString(r).Contains(";1R"));
        });

        await Assert.That(replyContainsCursorPositionReport).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Programmatic_input_on_the_model_raises_InputProduced() {
        // TerminalControlModel has no headless-testable keyboard event of its own (TerminalControl,
        // the Avalonia visual that turns real keypresses into Send(...) calls via
        // GenerateKeyInput/GenerateCharInput, cannot run headless). Model.Send(...) is the same UserInput event a real keypress handler raises,
        // so it's the correct headless-testable proxy for "keyboard input reaches InputProduced".
        var sawTypedByte = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            var replies = new List<byte[]>();
            surface.InputProduced += replies.Add;

            surface.Model.Send("a");

            return replies.Any(r => System.Text.Encoding.UTF8.GetString(r) == "a");
        });

        await Assert.That(sawTypedByte).IsTrue();
    }

    [Test]
    public async Task A_terminal_reset_clears_what_was_fed_before_it() {
        // The daemon repairs a desynced mirror with ESC c followed by a replay, so a remote
        // viewer that ignored the reset would paint the replay over stale content. The reset is
        // fed on its own and both rows are read before any replay text can overwrite them: a
        // reset that only homed the cursor would leave "stale" intact on the second row and the
        // tail of the first.
        var (before, rowsAfterReset, afterReplay) = await AvaloniaSession.DispatchAsync(() => {
            var surface = new XtermTerminalSurface(cols: 80, rows: 24);
            surface.Feed("stale-line\r\nstale");
            var engine = surface.Model.Terminal.Engine;
            var first  = (engine.GetLine(0), engine.GetLine(1));

            surface.Feed("\u001bc");
            var cleared = (engine.GetLine(0).Trim(), engine.GetLine(1).Trim());

            surface.Feed("fresh");

            return (first, cleared, (engine.GetLine(0), engine.GetLine(1).Trim()));
        });

        await Assert.That(before).IsEqualTo(("stale-line", "stale"));
        await Assert.That(rowsAfterReset).IsEqualTo(("", ""));
        await Assert.That(afterReplay).IsEqualTo(("fresh", ""));
    }
}
