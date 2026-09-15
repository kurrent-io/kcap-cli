using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Capacitor.App.Tests.Unit;

/// A clipboard payload the test writes by hand: one item per value, so Avalonia's own
/// TryGet*Async extensions do the reading exactly as they would over a platform transfer.
/// `gate` holds every read until the test releases it; `throwOnRead` fails them.
sealed class FakeAsyncDataTransfer : IAsyncDataTransfer {
    readonly List<DataFormat> _formats = [];
    readonly List<IAsyncDataTransferItem> _items = [];

    public FakeAsyncDataTransfer(
            string? text = null, Bitmap? bitmap = null, IReadOnlyList<IStorageItem>? files = null,
            bool throwOnRead = false, Task? gate = null) {
        if (text is not null) Add(DataFormat.Text, text, throwOnRead, gate);
        if (bitmap is not null) Add(DataFormat.Bitmap, bitmap, throwOnRead, gate);
        foreach (var file in files ?? []) Add(DataFormat.File, file, throwOnRead, gate);
    }

    public int Disposed { get; private set; }
    public IReadOnlyList<DataFormat> Formats => _formats;
    public IReadOnlyList<IAsyncDataTransferItem> Items => _items;
    public void Dispose() => Disposed++;

    void Add(DataFormat format, object value, bool throwOnRead, Task? gate) {
        if (!_formats.Contains(format)) _formats.Add(format);
        _items.Add(new Item(format, value, throwOnRead, gate));
    }

    sealed class Item(DataFormat format, object value, bool throwOnRead, Task? gate) : IAsyncDataTransferItem {
        public IReadOnlyList<DataFormat> Formats => [format];

        public async Task<object?> TryGetRawAsync(DataFormat requested) {
            if (gate is not null) await gate;
            if (throwOnRead) throw new InvalidOperationException("the clipboard is unreadable");
            return requested == format ? value : null;
        }
    }
}
