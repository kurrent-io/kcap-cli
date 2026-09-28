namespace Capacitor.Cli;

/// <summary>Reads a response body without ever pulling more than a fixed number of bytes; only
/// bounded when the request was sent with <see cref="HttpCompletionOption.ResponseHeadersRead"/>.</summary>
internal static class BoundedHttpContent {
    /// <summary>The body, or null when it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadAsync(HttpContent content, int maxBytes, CancellationToken ct) {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes + 1];
        var total  = 0;
        while (total < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        return total > maxBytes ? null : buffer.AsSpan(0, total).ToArray();
    }
}
