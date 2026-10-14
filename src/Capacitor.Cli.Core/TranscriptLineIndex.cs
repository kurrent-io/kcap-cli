namespace Capacitor.Cli.Core;

/// <summary>
/// The byte offset each transcript line starts at, learned as a watcher reads, so a poll seeks to
/// its line cursor instead of decoding the whole file again. Line numbers are only ever the ones a
/// full <see cref="StreamReader"/> pass gives: a lone <c>'\r'</c> also ends a line there, so the
/// index stops learning at one, and a UTF-16 or UTF-32 file is never indexed.
/// </summary>
/// <remarks>
/// Only the bytes just before the indexed end are compared on each poll: a file that shrank below
/// it or changed there drops the index and is read from byte 0. A rewrite further back goes
/// unnoticed, which the line cursor already relies on never happening — a transcript is appended
/// to, never edited. One reader at a time.
/// </remarks>
sealed class TranscriptLineIndex {
    const int TailLength = 4096;

    // _starts[i] is where line i starts; the last entry is the end of the last indexed line.
    readonly List<long> _starts = [0];

    byte[] _tail    = [];
    long   _tailEnd;
    bool   _unsupported;

    long _position;
    bool _scanning;
    bool _pendingCr;

    long Frontier => _starts[^1];

    /// <summary>
    /// Validates the index against <paramref name="stream"/> and returns the line and byte to
    /// start decoding at for a reader whose cursor is <paramref name="line"/>.
    /// </summary>
    public async ValueTask<(int Line, long Offset)> StartAsync(FileStream stream, long length, int line, CancellationToken ct) {
        if (Frontier > 0 && (Frontier != _tailEnd || length < Frontier || !await TailMatchesAsync(stream, ct))) Reset();

        // An unsupported file is read from byte 0 every time, so its encoding is checked again.
        var from = _unsupported ? 0 : Math.Min(line, _starts.Count - 1);
        _unsupported = false;
        _position    = _starts[from];
        _scanning    = true;
        _pendingCr   = false;

        return (from, _position);
    }

    /// <summary>Learns the line ends in the next bytes read after the offset <see cref="StartAsync"/> returned.</summary>
    public void Observe(ReadOnlySpan<byte> bytes) {
        if (!_scanning || bytes.IsEmpty) {
            _position += bytes.Length;
            return;
        }

        if (_position == 0 && bytes[0] is 0xFE or 0xFF or 0x00) {
            _unsupported = true;
            _scanning    = false;
            Reset();
            return;
        }

        var skip = (int)Math.Clamp(Frontier - _position, 0, bytes.Length);
        var span = bytes[skip..];
        var at   = _position + skip;
        _position += bytes.Length;

        if (_pendingCr && span.Length > 0 && span[0] != (byte)'\n') {
            _scanning = false;
            return;
        }

        while (span.Length > 0) {
            var i = span.IndexOfAny((byte)'\r', (byte)'\n');
            if (i < 0) {
                _pendingCr = false;
                return;
            }

            if (span[i] == (byte)'\n') {
                _starts.Add(at + i + 1);
                _pendingCr = false;
            } else if (i + 1 == span.Length) {
                _pendingCr = true;
                return;
            } else if (span[i + 1] != (byte)'\n') {
                _scanning = false;
                return;
            }

            at   += i + 1;
            span  = span[(i + 1)..];
        }
    }

    /// <summary>Records the bytes before the new indexed end, for the next poll to compare.</summary>
    public async ValueTask FinishAsync(FileStream stream, CancellationToken ct) {
        _scanning = false;
        if (_unsupported || Frontier == _tailEnd) return;

        var tail = new byte[(int)Math.Min(TailLength, Frontier)];
        if (await ReadAtAsync(stream, Frontier - tail.Length, tail, ct) != tail.Length) {
            Reset();
            return;
        }

        _tail    = tail;
        _tailEnd = Frontier;
    }

    async ValueTask<bool> TailMatchesAsync(FileStream stream, CancellationToken ct) {
        var current = new byte[_tail.Length];

        return await ReadAtAsync(stream, _tailEnd - _tail.Length, current, ct) == current.Length
            && current.AsSpan().SequenceEqual(_tail);
    }

    static async ValueTask<int> ReadAtAsync(FileStream stream, long offset, byte[] buffer, CancellationToken ct) {
        stream.Position = offset;
        var total = 0;

        while (total < buffer.Length) {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    void Reset() {
        _starts.Clear();
        _starts.Add(0);
        _tail    = [];
        _tailEnd = 0;
    }
}
