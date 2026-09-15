using System.Reactive;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One chip in the strip. An image gets a thumbnail decoded off the UI thread; anything else — and
/// anything the decoder refuses — keeps the extension glyph. A thumbnail that lands after the chip
/// is gone is freed rather than published, so a removed chip leaves no bitmap behind.
public sealed class StagedAttachmentViewModel : ReactiveObject, IDisposable {
    const int ThumbnailWidth = 64;

    readonly CancellationTokenSource _decoding = new();
    Bitmap? _thumbnail;
    bool _disposed;

    public StagedAttachmentViewModel(StagedAttachment file, Action<StagedAttachment> remove) {
        File = file;
        RemoveCommand = ReactiveCommand.Create(() => remove(file));
        if (file.IsImage) _ = DecodeAsync();
    }

    public StagedAttachment File { get; }
    public string FileName => File.FileName;
    public string SizeLabel => File.SizeLabel;
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// The extension, for a chip with no thumbnail to show.
    public string Glyph {
        get {
            var extension = Path.GetExtension(File.FileName).TrimStart('.');
            return extension.Length is > 0 and <= 4 ? extension.ToUpperInvariant() : "FILE";
        }
    }

    public Bitmap? Thumbnail {
        get => _thumbnail;
        private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
    }

    async Task DecodeAsync() {
        var bytes = File.Bytes;
        Bitmap? decoded;
        try {
            decoded = await Task.Run(() => {
                using var stream = new MemoryStream(bytes.ToArray());
                return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
            }, _decoding.Token).ConfigureAwait(false);
        } catch (Exception) {
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (_disposed) decoded.Dispose();
            else Thumbnail = decoded;
        });
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _decoding.Cancel();
        _decoding.Dispose();
        RemoveCommand.Dispose();
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
