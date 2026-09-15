using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Turns clipboard, drop and picker payloads into staged chips. Pure over Avalonia's data-transfer and
/// storage types so it needs no clipboard to test.
public static class AttachmentIntake {
    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".md"] = "text/plain", [".json"] = "application/json",
        [".csv"] = "text/csv", [".log"] = "text/plain", [".xml"] = "application/xml", [".yaml"] = "text/plain", [".yml"] = "text/plain",
        [".cs"] = "text/plain", [".ts"] = "text/plain", [".js"] = "text/plain", [".py"] = "text/plain", [".go"] = "text/plain",
        [".rs"] = "text/plain", [".java"] = "text/plain", [".rb"] = "text/plain", [".sh"] = "text/plain", [".sql"] = "text/plain",
        [".html"] = "text/plain", [".css"] = "text/plain", [".toml"] = "text/plain", [".ini"] = "text/plain",
    };

    public static IntakeKind Classify(IReadOnlyList<DataFormat> formats, bool hasNonBlankText) {
        if (formats.Contains(DataFormat.File)) return IntakeKind.Files;
        if (formats.Contains(DataFormat.Text) && hasNonBlankText) return IntakeKind.Text;
        if (formats.Contains(DataFormat.Bitmap)) return IntakeKind.Bitmap;
        return IntakeKind.Nothing;
    }

    public static string ContentTypeFor(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    public static async Task<IntakeResult> ReadFilesAsync(IEnumerable<IStorageItem> items, CancellationToken ct) {
        var accepted = new List<StagedAttachment>();
        var refused = new List<IntakeRefusal>();
        foreach (var item in items) {
            ct.ThrowIfCancellationRequested();
            if (item is not IStorageFile file) { refused.Add(new(item.Name, "is a folder")); continue; }
            try {
                var props = await file.GetBasicPropertiesAsync().ConfigureAwait(false);
                if (props.Size is { } size && size > (ulong)InputWire.MaxAttachmentBytes) { refused.Add(new(file.Name, "is over 10 MB")); continue; }
                await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
                var bytes = await ReadCappedAsync(stream, ct).ConfigureAwait(false);
                if (bytes is null) { refused.Add(new(file.Name, "is over 10 MB")); continue; }
                accepted.Add(new StagedAttachment(file.Name, ContentTypeFor(file.Name), bytes));
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception) {
                refused.Add(new(item.Name, "could not be read"));
            }
        }
        return new IntakeResult(accepted, refused);
    }

    public static IntakeResult FromBitmap(Bitmap bitmap, TimeProvider time) {
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return FromPngBytes(ms.ToArray(), time);
    }

    internal static IntakeResult FromPngBytes(byte[] png, TimeProvider time) {
        var name = $"pasted-image-{time.GetUtcNow():yyyyMMdd-HHmmss}.png";
        return png.LongLength > InputWire.MaxAttachmentBytes
            ? new IntakeResult([], [new(name, "is over 10 MB")])
            : new IntakeResult([new StagedAttachment(name, "image/png", png)], []);
    }

    /// Null when the stream runs past the cap; reading stops at cap + 1 bytes.
    static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken ct) {
        var limit = InputWire.MaxAttachmentBytes + 1;
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (ms.Length < limit) {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - ms.Length)), ct).ConfigureAwait(false);
            if (read == 0) return ms.ToArray();
            ms.Write(buffer, 0, read);
        }
        return null;
    }
}
