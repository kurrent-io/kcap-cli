using System.Text.Json;
using Capacitor.Cli.Capture;

namespace Capacitor.Cli.Tests.Unit.Capture;

public class TranscriptCaptureTests {
    [Test]
    public async Task LargeMalformedInputBecomesSafeVersionedMarker() {
        var raw = new string('x', 70_000) + " ghp_0123456789abcdef";
        var result = TranscriptCapture.Encode(raw);
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
}
