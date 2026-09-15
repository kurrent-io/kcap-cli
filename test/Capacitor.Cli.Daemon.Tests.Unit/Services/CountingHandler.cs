using System.Net;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Answers any request with a body of <paramref name="totalBytes"/> bytes and counts what the caller
/// actually pulls, so a test can pin where reading stopped rather than only what was kept. Without a
/// <paramref name="declaredLength"/> the body arrives chunked; with one the response advertises that
/// length whatever the body really holds.
internal sealed class CountingHandler(long totalBytes, long? declaredLength = null) : HttpMessageHandler {
    long _read;

    public long BytesRead => Interlocked.Read(ref _read);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var content = new StreamContent(new ZeroStream(totalBytes, n => Interlocked.Add(ref _read, n)));
        content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"big.bin\"");

        if (declaredLength is { } declared) content.Headers.ContentLength = declared;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = content, RequestMessage = request
        });
    }

    /// Unseekable and without a length, so StreamContent advertises no Content-Length of its own.
    sealed class ZeroStream(long total, Action<int> counted) : Stream {
        long _position;

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();

        public override long Position {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            var n = (int)Math.Min(buffer.Length, total - _position);

            if (n <= 0) return 0;

            buffer[..n].Clear();
            _position += n;
            counted(n);

            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
