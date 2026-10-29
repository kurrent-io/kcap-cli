using System.Buffers;

namespace Capacitor.Cli.Capture;

internal sealed class BoundedJsonBufferWriter : IBufferWriter<byte>, IDisposable {
    readonly int _limit;
    byte[] _buffer;
    int _written;

    public BoundedJsonBufferWriter(int limit) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        _buffer = ArrayPool<byte>.Shared.Rent(limit);
    }

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _limit - _written) throw new RedactionOutputLimitException();
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (Math.Max(1, sizeHint) > _limit - _written) throw new RedactionOutputLimitException();
        return _buffer.AsMemory(_written, _limit - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    public void Dispose() {
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = [];
        _written = 0;
    }
}
