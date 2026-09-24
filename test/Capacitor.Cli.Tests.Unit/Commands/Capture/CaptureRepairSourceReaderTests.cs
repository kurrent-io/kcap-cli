using System.Text.Json;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Commands.Capture;
using Capacitor.Cli.Commands.Capture.Wire;

namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

public class CaptureRepairSourceReaderTests {
    [Test]
    public async Task Rejects_changed_scan_bytes_even_when_the_file_is_restored_before_eof() {
        using var tmp = new TempDir();
        var lines = Enumerable.Repeat("{\"text\":\"" + new string('a', 10000) + "\"}", 5).ToArray();
        var original = string.Join('\n', lines) + "\n";
        var path = tmp.CreateFile("root.jsonl", original);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var observedChange = false;
        await Assert.That(async () => await CaptureRepairSourceReader.ReadAsync(path, async (line, ct) => {
            if (line.Number == 0) {
                lines[2] = lines[2].Replace('a', 'b');
                await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", ct);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
            if (line.Number == 2) {
                observedChange = line.RedactedJson.Contains(new string('b', 100), StringComparison.Ordinal);
                await File.WriteAllTextAsync(path, original, ct);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
        }, default)).Throws<IOException>();
        await Assert.That(observedChange).IsTrue();
    }

    [Test]
    public async Task Rejects_same_length_changes_with_the_original_timestamp() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("root.jsonl", "{}\n{}\n");
        var timestamp = File.GetLastWriteTimeUtc(path);
        await Assert.That(async () => await CaptureRepairSourceReader.ReadAsync(path, async (line, ct) => {
            if (line.Number != 0) return;
            await File.WriteAllTextAsync(path, "{}\n[]\n", ct);
            File.SetLastWriteTimeUtc(path, timestamp);
        }, default)).Throws<IOException>();
    }

    [Test]
    public async Task Reads_every_coordinate_and_redacts_large_unicode_records_before_the_callback() {
        using var tmp = new TempDir();
        var secret = "ghp_" + new string('a', 36);
        var raw = "{\"text\":\"漢🙂" + new string('x', 70000) + "\",\"token\":\"" + secret + "\"}";
        var path = tmp.CreateFile("root.jsonl", raw + "\n\n{\"done\":true}\n");
        var lines = new List<CaptureRepairLineRequest>();
        var last = await CaptureRepairSourceReader.ReadAsync(path, (line, _) => { lines.Add(line); return Task.CompletedTask; }, default);
        await Assert.That(last).IsEqualTo(2);
        await Assert.That(lines.Select(x => x.Number).ToArray()).IsEquivalentTo(new[] { 0, 1, 2 });
        await Assert.That(lines[0].OriginalUtf16Length).IsEqualTo(raw.Length);
        await Assert.That(lines[0].RedactedJson).DoesNotContain(secret);
        using var parsed = JsonDocument.Parse(lines[0].RedactedJson);
        await Assert.That(parsed.RootElement.Str("text")).IsEqualTo("漢🙂" + new string('x', 70000));
    }

    [Test]
    public async Task Reports_bounded_loss_reasons_without_exposing_raw_records() {
        using var tmp = new TempDir();
        var raw = new string('x', 70000);
        var path = tmp.CreateFile("root.jsonl", raw + "\n" + raw + "\n");
        var losses = new List<RedactionLossReason>();
        var lines = new List<CaptureRepairLineRequest>();
        await CaptureRepairSourceReader.ReadAsync(path, (line, _) => { lines.Add(line); return Task.CompletedTask; }, losses.Add, default);
        await Assert.That(losses.ToArray()).IsEquivalentTo(new[] { RedactionLossReason.MalformedInput, RedactionLossReason.MalformedInput });
        await Assert.That(lines.All(line => line.RedactedJson.Contains("kcap_capture_loss", StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.All(line => !line.RedactedJson.Contains(raw, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rejects_a_source_appended_or_truncated_during_the_scan(bool truncate) {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("root.jsonl", "{}\n{}\n");
        await Assert.That(async () => await CaptureRepairSourceReader.ReadAsync(path, async (line, ct) => {
            if (line.Number != 0) return;
            if (truncate) await File.WriteAllTextAsync(path, "", ct);
            else await File.AppendAllTextAsync(path, "{}\n", ct);
        }, default)).Throws<IOException>();
    }

    [Test]
    public async Task Missing_child_and_cancellation_fail_before_declaring_eof() {
        using var tmp = new TempDir();
        await Assert.That(async () => await CaptureRepairSourceReader.ReadAsync(tmp.PathTo("missing.jsonl"), (_, _) => Task.CompletedTask, default))
            .Throws<IOException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = tmp.CreateFile("root.jsonl", "{}\n");
        await Assert.That(async () => await CaptureRepairSourceReader.ReadAsync(path, (_, _) => Task.CompletedTask, cancellation.Token))
            .Throws<OperationCanceledException>();
    }
}
