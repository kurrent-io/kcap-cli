using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Svg;
using Svg.Model;
using SvgImage = Avalonia.Svg.Skia.SvgImage;

namespace Capacitor.App.Services;

internal sealed class ToolPreviewImages : IDisposable {
    internal const int MaxBytes = 20 * 1024 * 1024;
    const int MaxEntries = 8;
    static readonly object Gate = new();
    static readonly Dictionary<string, ToolPreviewImages> Cache = new(StringComparer.Ordinal);
    readonly object _source;
    readonly long _length;
    readonly DateTime _modified;
    int _references = 1;

    ToolPreviewImages(object source, FileInfo info) {
        _source = source;
        _length = info.Length;
        _modified = info.LastWriteTimeUtc;
    }

    internal IImage Image => _source is Bitmap bitmap ? bitmap : new SvgImage { Source = (SvgSource)_source };

    // The cache owns one reference; a displayed or in-flight preview owns another.
    internal static ToolPreviewImages? Acquire(string path) {
        lock (Gate) {
            try {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > MaxBytes) return null;
                if (Cache.TryGetValue(path, out var cached)) {
                    if (cached._length == info.Length && cached._modified == info.LastWriteTimeUtc) {
                        Interlocked.Increment(ref cached._references);
                        return cached;
                    }
                    Cache.Remove(path);
                    cached.Dispose();
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var buffer = ReadBounded(stream);
                if (buffer is null || buffer.Length == 0) return null;
                object? source = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    ? SvgSource.LoadFromStream(buffer, new SvgParameters(null, null, LoadOptions: new SvgDocumentLoadOptions {
                        ProcessingMode = SvgProcessingMode.SecureStatic,
                        ExternalResources = SvgExternalResourcePolicy.SameDocumentAndDataOnly,
                    }))
                    : Bitmap.DecodeToWidth(buffer, 960);
                if (source is null) return null;
                if (Cache.Count >= MaxEntries) {
                    var first = Cache.First();
                    Cache.Remove(first.Key);
                    first.Value.Dispose();
                }
                var result = new ToolPreviewImages(source, info);
                Cache.Add(path, result);
                Interlocked.Increment(ref result._references);
                return result;
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: image preview failed: {ex.Message}");
                return null;
            }
        }
    }

    internal static MemoryStream? ReadBounded(Stream stream) {
        var result = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(chunk)) > 0) {
            if (result.Length + read > MaxBytes) {
                result.Dispose();
                return null;
            }
            result.Write(chunk, 0, read);
        }
        result.Position = 0;
        return result;
    }

    internal static void Clear() {
        lock (Gate) {
            foreach (var image in Cache.Values) image.Dispose();
            Cache.Clear();
        }
    }

    public void Dispose() {
        if (Interlocked.Decrement(ref _references) == 0 && _source is IDisposable disposable) disposable.Dispose();
    }
}
