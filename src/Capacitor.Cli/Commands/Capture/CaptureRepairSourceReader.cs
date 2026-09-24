using System.Text;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Commands.Capture.Wire;

namespace Capacitor.Cli.Commands.Capture;

internal static class CaptureRepairSourceReader {
    public static Task<int> ReadAsync(string path,
        Func<CaptureRepairLineRequest, CancellationToken, Task> consume, CancellationToken ct) =>
        ReadAsync(path, consume, null, ct);

    public static async Task<int> ReadAsync(string path,
        Func<CaptureRepairLineRequest, CancellationToken, Task> consume,
        Action<RedactionLossReason>? reportLoss, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var snapshot = CaptureRepairFileSnapshot.Take(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var number = 0;
        while (await reader.ReadLineAsync(ct) is { } raw) {
            snapshot.AssertUnchanged();
            if (number == 0 && raw.StartsWith('\uFEFF')) raw = raw[1..];
            var safe = TranscriptCapture.Encode(raw);
            if (safe.Loss is { } reason) reportLoss?.Invoke(reason);
            await consume(new(number++, safe.Line, raw.Length), ct);
        }
        ct.ThrowIfCancellationRequested();
        snapshot.AssertUnchanged();
        if (input.Length != snapshot.Length) throw new IOException("Transcript changed during recovery; retry after the session ends.");
        return number - 1;
    }
}
