using System.Text;

namespace Capacitor.Cli.Core;

public enum TailStatus { Ok, Reset, Missing, Failed }

public sealed record TailRead(IReadOnlyList<string> Lines, TailStatus Status, string? Failure = null) {
    /// Byte positions at the start of each returned line, for correlating newly appended input.
    public IReadOnlyList<long> LineStartOffsets { get; init; } = [];
    /// Length observed when this read began, including a partial final line.
    public long? SnapshotLength { get; init; }
}

/// Appended-lines reader over a JSONL file another process is writing. Every open shares
/// read/write/delete: a FileShare.Read open would deny the agent its own write handle on
/// Windows. Only a length regression resets the cursor — a replacement by a same-or-longer
/// file is read from the old cursor, which both vendors' append-only transcripts never produce.
public sealed class JsonlTail(string path) {
    long _cursor;

    public string Path { get; } = path;
    public long Cursor => _cursor;

    public TailRead ReadAppended() {
        try {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var regressed = length < _cursor;
            // The origin stays local until the read succeeds: assigning the reset cursor up front
            // would let a fault mid-read return Failed with the regression already spent, and the
            // next Ok read would then deliver the whole file on top of what it had reported.
            var origin = regressed ? 0 : _cursor;
            var status = regressed ? TailStatus.Reset : TailStatus.Ok;
            if (length == origin) {
                _cursor = origin;
                return new TailRead([], status) { SnapshotLength = length };
            }

            stream.Position = origin;
            var buffer = new byte[length - origin];
            var read = 0;
            while (read < buffer.Length) {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }

            var offsets = new List<long>();
            var lines = SplitCompleteLines(buffer.AsSpan(0, read), out var consumed, offsets, origin);
            _cursor = origin + consumed;
            return new TailRead(lines, status) { LineStartOffsets = offsets, SnapshotLength = length };
        } catch (FileNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (DirectoryNotFoundException) {
            return new TailRead([], TailStatus.Missing);
        } catch (Exception ex) {
            return new TailRead([], TailStatus.Failed, ex.Message);
        }
    }

    /// Complete lines only; `consumed` stops after the last '\n' so an unterminated tail is
    /// re-read whole once its newline lands.
    public static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed) =>
        SplitCompleteLines(bytes, out consumed, null, 0);

    static List<string> SplitCompleteLines(ReadOnlySpan<byte> bytes, out int consumed, List<long>? offsets, long origin) {
        var lines = new List<string>();
        consumed = 0;
        var start = 0;
        for (var i = 0; i < bytes.Length; i++) {
            if (bytes[i] != (byte)'\n') continue;
            var line = bytes[start..i];
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!IsBlank(line)) {
                lines.Add(Encoding.UTF8.GetString(line));
                offsets?.Add(origin + start);
            }
            start = i + 1;
            consumed = start;
        }
        return lines;
    }

    static bool IsBlank(ReadOnlySpan<byte> line) {
        foreach (var b in line) {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        }
        return true;
    }
}
