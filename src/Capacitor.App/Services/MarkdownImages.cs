using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.Threading;

namespace Capacitor.App.Services;

/// The one place the app fetches an image named in markdown. Only an absolute https URL is
/// fetched, with no cookies or credentials, under a byte cap and a timeout, and the outcome is
/// kept for the process: a details toggle re-renders the whole document, and a URL that failed
/// once is not retried on every toggle.
public static class MarkdownImages {
    public const int MaxBytes = 8 * 1024 * 1024;
    const int MaxEntries = 512;

    static readonly ConcurrentDictionary<string, Task<IImage?>> Cache = new(StringComparer.Ordinal);

    static readonly Lazy<HttpClient> Http = new(() => {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("kcap-desktop", "1"));
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
        return client;
    });

    /// Downloads a URL's bytes, or null when it cannot be shown. Replaced by tests, which have no
    /// network to reach.
    public static Func<string, CancellationToken, Task<byte[]?>> Fetch { get; set; } = FetchOverHttps;

    public static bool CanLoad(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public static Task<IImage?> LoadAsync(string url) {
        if (!CanLoad(url)) return Task.FromResult<IImage?>(null);
        if (Cache.Count >= MaxEntries) Cache.Clear();
        return Cache.GetOrAdd(url, static u => LoadUncachedAsync(u));
    }

    public static void Clear() => Cache.Clear();

    static async Task<IImage?> LoadUncachedAsync(string url) {
        try {
            var bytes = await Fetch(url, CancellationToken.None).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return null;
            return await DecodeAsync(bytes).ConfigureAwait(false);
        } catch (Exception) {
            return null;
        }
    }

    /// An SVG is recognised by its text, not its URL: badge services serve one from a path with
    /// no extension. The Avalonia-side image object is built on the UI thread, which is the only
    /// thread allowed to set its properties.
    static async Task<IImage?> DecodeAsync(byte[] bytes) {
        if (LooksLikeSvg(bytes)) {
            var source = SvgSource.LoadFromStream(new MemoryStream(bytes), null);
            if (source is null) return null;
            return await Dispatcher.UIThread.InvokeAsync(() => (IImage)new SvgImage { Source = source });
        }
        return new Bitmap(new MemoryStream(bytes));
    }

    static bool LooksLikeSvg(byte[] bytes) {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            || head.StartsWith("<!DOCTYPE svg", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<byte[]?> FetchOverHttps(string url, CancellationToken cancellation) {
        using var response = await Http.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0) {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
