using System.Text.Json;

namespace Capacitor.Cli.Core.PullRequests;

/// Read-only view over a response body that refuses to deliver more than `limit` bytes, throwing
/// JsonException on the first byte past it so a body with no Content-Length is held to the same
/// cap as a declared one. Disposal is the caller's: the view does not own the body.
sealed class CappedReadStream(Stream body, int limit) : Stream {
    long _delivered;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => Delivered(body.Read(buffer[..Room(buffer.Length)]));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => Delivered(await body.ReadAsync(buffer[..Room(buffer.Length)], cancellationToken).ConfigureAwait(false));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // One byte of room past the cap is what tells a body at the cap from one over it.
    int Room(int requested) => (int)Math.Min(requested, limit + 1 - _delivered);

    int Delivered(int read) {
        _delivered += read;
        if (_delivered > limit) throw new JsonException("Response body exceeds the size cap.");
        return read;
    }
}
