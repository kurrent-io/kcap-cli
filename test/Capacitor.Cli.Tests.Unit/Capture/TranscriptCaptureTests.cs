using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Capture;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Capture;

// The record budget and the watcher's regex deadline are wall-clock. A frozen clock and an
// unbounded match keep a loaded runner from reporting RecordBudget or RegexTimeout in place of
// the reason under test.
public class TranscriptCaptureTests {
    static readonly FakeTimeProvider Clock = new();

    static readonly SecretPatterns Unbounded = SecretRedactor.WatcherPatterns.WithMatchTimeout(Regex.InfiniteMatchTimeout);

    [Test]
    [Arguments("not json")]
    [Arguments("{\"token\":\"ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"")]
    public async Task Small_malformed_records_become_explicit_loss_markers(string raw) {
        var result = TranscriptCapture.Encode(raw, Clock, Unbounded);
        await Assert.That(result.Loss).IsEqualTo(RedactionLossReason.MalformedInput);
        using var marker = JsonDocument.Parse(result.Line);
        await Assert.That(marker.RootElement.GetProperty("reason").GetString()).IsEqualTo("malformed_input");
        await Assert.That(result.Line).DoesNotContain(raw);
    }

    [Test]
    public async Task LargeMalformedInputBecomesSafeVersionedMarker() {
        var raw = new string('x', 70_000) + " ghp_0123456789abcdef";
        var result = TranscriptCapture.Encode(raw, Clock, Unbounded);
        using var marker = JsonDocument.Parse(result.Line);
        await Assert.That(result.Loss).IsEqualTo(RedactionLossReason.MalformedInput);
        await Assert.That(result.Line.Contains("ghp_", StringComparison.Ordinal)).IsFalse();
        await Assert.That(marker.RootElement.GetProperty("version").GetInt32()).IsEqualTo(1);
        await Assert.That(marker.RootElement.GetProperty("reason").GetString()).IsEqualTo("malformed_input");
        await Assert.That(marker.RootElement.GetProperty("input_utf8_bytes").GetInt32()).IsEqualTo(raw.Length);
    }

    [Test]
    [Arguments(RedactionLossReason.InputLimit, "input_limit")]
    [Arguments(RedactionLossReason.MalformedInput, "malformed_input")]
    [Arguments(RedactionLossReason.RegexTimeout, "regex_timeout")]
    [Arguments(RedactionLossReason.RecordBudget, "record_budget")]
    [Arguments(RedactionLossReason.OutputLimit, "output_limit")]
    public async Task MarkerUsesTheCaptureProtocolReason(RedactionLossReason reason, string expected) {
        var marker = new CaptureLossMarker { Reason = CaptureLossMarker.ReasonName(reason), InputUtf16Length = 12, InputUtf8Bytes = 18 };
        var json = JsonSerializer.Serialize(marker, CaptureJsonContext.Default.CaptureLossMarker);
        using var parsed = JsonDocument.Parse(json);
        await Assert.That(parsed.RootElement.GetProperty("reason").GetString()).IsEqualTo(expected);
        await Assert.That(parsed.RootElement.GetProperty("type").GetString()).IsEqualTo("kcap_capture_loss");
    }

    [Test]
    public async Task A_tail_stops_at_a_line_its_budget_cannot_redact() {
        var clock = new SwitchableRedactionClock();
        var losses = new List<RedactionLossReason>();

        var tail = TranscriptCapture.EncodeTail(["{\"token\":\"x\"}", "{\"a\":1}"], [0, 1],
            new RedactionBudget(clock, TimeSpan.FromSeconds(1)), (_, _) => null, (reason, _) => losses.Add(reason));

        await Assert.That(tail.Consumed).IsEqualTo(0);
        await Assert.That(tail.Lines.Count).IsEqualTo(0);
        await Assert.That(losses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_recorded_line_redacts_where_the_watcher_budget_runs_out() {
        const string raw = "{\"token\":\"ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}";

        var watcher  = TranscriptCapture.Encode(raw, new SteppingRedactionClock(TimeSpan.FromSeconds(2)));
        var recorded = TranscriptCapture.EncodeRecorded(raw, new SteppingRedactionClock(TimeSpan.FromSeconds(2)));

        await Assert.That(watcher.Loss).IsEqualTo(RedactionLossReason.RecordBudget);
        await Assert.That(recorded.Loss).IsNull();
        await Assert.That(recorded.Line).Contains("[REDACTED]");
        await Assert.That(recorded.Line).DoesNotContain("ghp_");
    }

    [Test]
    public async Task Encoding_until_held_stops_at_a_line_its_budget_cannot_redact() {
        var losses = new List<RedactionLossReason>();

        var captured = TranscriptCapture.EncodeUntilHeld(["{\"token\":\"x\"}", "{\"a\":1}"], () => false, new SwitchableRedactionClock(),
            (reason, _) => losses.Add(reason));

        await Assert.That(captured.Consumed).IsEqualTo(0);
        await Assert.That(captured.Lines.Count).IsEqualTo(0);
        await Assert.That(losses.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Encoding_until_held_marks_a_line_lost_for_what_it_is() {
        var losses = new List<RedactionLossReason>();

        var captured = TranscriptCapture.EncodeUntilHeld(["not json", "{\"a\":1}"], () => false, Clock, (reason, _) => losses.Add(reason), Unbounded);

        await Assert.That(captured.Consumed).IsEqualTo(2);
        await Assert.That(captured.Lines[0]).Contains("kcap_capture_loss");
        await Assert.That(captured.Lines[0]).DoesNotContain("not json");
        await Assert.That(captured.Lines[1]).IsEqualTo("{\"a\":1}");
        await Assert.That(losses).IsEquivalentTo([RedactionLossReason.MalformedInput]);
    }

    [Test]
    public async Task Encoding_until_held_stops_when_the_callers_time_is_up_after_one_line() {
        var captured = TranscriptCapture.EncodeUntilHeld(["{\"a\":1}", "{\"b\":2}"], () => true, Clock, (_, _) => { }, Unbounded);

        await Assert.That(captured.Consumed).IsEqualTo(1);
        await Assert.That(captured.Lines[0]).IsEqualTo("{\"a\":1}");
    }
}
