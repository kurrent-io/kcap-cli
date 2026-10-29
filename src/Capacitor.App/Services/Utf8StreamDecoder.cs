namespace Capacitor.App.Services;

using System.Text;

/// One incremental UTF-8 stream per attach attempt: PTY frames split multibyte
/// code points at arbitrary boundaries, and the terminal control's Feed does a
/// fresh GetString per call — this is the single decoder spanning the snapshot
/// and every live frame, flushed only at terminal completion.
public sealed class Utf8StreamDecoder {
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    /// Decodes straight into the result string: a reattach replays a ring of megabytes, and any scratch
    /// buffer between the bytes and the string is another copy of it. Delivery is strictly sequential.
    public unsafe string Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return "";
        var count = _decoder.GetCharCount(bytes, flush: false);
        if (count == 0) {
            // Only the start of a split code point: the decoder holds it for the next frame.
            _decoder.GetChars(bytes, Span<char>.Empty, flush: false);
            return "";
        }
        fixed (byte* source = bytes) {
            return string.Create(count, ((IntPtr)source, bytes.Length, _decoder), static (destination, state) =>
                state._decoder.GetChars(new ReadOnlySpan<byte>((byte*)state.Item1, state.Item2), destination, flush: false));
        }
    }

    /// Bytes per piece: at most as many characters, so every piece stays under the 85 KB large-object threshold.
    const int PieceBytes = 32 * 1024;

    /// <see cref="Decode"/> in pieces, for a replay of megabytes that would otherwise be one
    /// large-object string.
    public IReadOnlyList<string> DecodePieces(ReadOnlySpan<byte> bytes) {
        var pieces = new List<string>(bytes.Length / PieceBytes + 1);
        for (var start = 0; start < bytes.Length; start += PieceBytes) {
            var piece = Decode(bytes.Slice(start, Math.Min(PieceBytes, bytes.Length - start)));
            if (piece.Length > 0) pieces.Add(piece);
        }
        return pieces;
    }

    public string Flush() {
        var chars = new char[4];
        var n = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        return new string(chars, 0, n);
    }
}
