using System.Diagnostics;

namespace Capacitor.Cli;

/// <summary>
/// A detached child's stdin pipe that also owns the <see cref="Process"/> wrapper behind it, so
/// closing the stream releases that wrapper's process handle in the same breath. A caller of the
/// detached start is handed nothing but this stream, which makes the stream the only place that
/// wrapper's lifetime can be tied off. Disposing it leaves the child itself running.
/// </summary>
sealed class ChildStdinStream(Stream pipe, Process owner) : Stream {
    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()                                     => pipe.Flush();
    public override Task FlushAsync(CancellationToken ct)            => pipe.FlushAsync(ct);
    public override void Write(byte[] buffer, int offset, int count) => pipe.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer)            => pipe.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        pipe.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        pipe.WriteAsync(buffer, ct);

    public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing) {
            pipe.Dispose();
            owner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync() {
        await pipe.DisposeAsync();
        owner.Dispose();

        await base.DisposeAsync();
    }
}
