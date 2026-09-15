namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A local-socket stand-in: the daemon reads one stream and writes to another, so a test
/// can seed the frames it receives and inspect the frames it sent.</summary>
sealed class DuplexTestStream(Stream readSide, Stream writeSide) : Stream {
    /// <summary>The daemon's write side, for tests that need to inspect frames it sent.</summary>
    public Stream WrittenStream => writeSide;

    public override int Read(byte[] b, int o, int c) => readSide.Read(b, o, c);
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => readSide.ReadAsync(b, ct);
    public override void Write(byte[] b, int o, int c) => writeSide.Write(b, o, c);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => writeSide.WriteAsync(b, ct);
    public override void Flush() => writeSide.Flush();
    public override Task FlushAsync(CancellationToken ct) => writeSide.FlushAsync(ct);
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set { } }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
        if (disposing) { readSide.Dispose(); writeSide.Dispose(); }
        base.Dispose(disposing);
    }
}
