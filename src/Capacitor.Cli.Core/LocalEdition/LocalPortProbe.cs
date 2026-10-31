using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Who answers on the local server's port, told from the answer alone: no process lookup, so it reads the
/// same on every platform and for another user's process. <c>kcap local</c> and the desktop app both ask through
/// here, so they cannot classify one port two ways.</summary>
public static class LocalPortProbe {
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    const int MaxNameLength = 80;

    const int MaxBodyBytes = 16 * 1024;

    /// <summary>One request, never a wait. Throws only <see cref="OperationCanceledException"/> from
    /// <paramref name="ct"/>. The client must not follow redirects: a redirect is an answer, and a foreign one.</summary>
    /// <remarks>Asks <c>127.0.0.1</c>, which the server listens on and its Host policy admits, not <c>localhost</c>:
    /// Windows takes about two seconds to refuse <c>::1</c>, most of the request's budget.</remarks>
    public static async Task<LocalPortAnswer> ReadAsync(HttpClient http, int port, TimeProvider time, CancellationToken ct) {
        using var attempt = new CancellationTokenSource(RequestTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token);

        try {
            using var response = await http.GetAsync(
                new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/healthz/ready"),
                HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (Foreign(response) is { } foreign) return foreign;

            return new LocalPortAnswer.Answered(response.StatusCode, await ReadBodyAsync(response.Content, linked.Token));
        } catch (HttpRequestException ex) {
            return Classify(ex);
        } catch (IOException ex) {
            return new LocalPortAnswer.Silent(ex.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new LocalPortAnswer.Silent($"no answer within {RequestTimeout.TotalSeconds:0} seconds");
        }
    }

    /// <summary>The kcap server's health route answers 200 when ready and 503 when not, and any server can fail with
    /// another 5xx or shed load with a 429. Its host, Kestrel, answers 400, 401, 404 and the like before the route is
    /// mapped or for a request its host policy refuses, so nothing that calls itself Kestrel is foreign. Every other
    /// status is another program's.</summary>
    static LocalPortAnswer.Foreign? Foreign(HttpResponseMessage response) {
        var status = (int)response.StatusCode;
        var name   = response.Headers.TryGetValues("Server", out var values) ? string.Join(' ', values).Trim() : "";

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TooManyRequests || status >= 500
         || name.Contains("Kestrel", StringComparison.OrdinalIgnoreCase))
            return null;

        if (name.Length > MaxNameLength) name = name[..MaxNameLength] + "…";

        return new(name.Length > 0
            ? $"a web server that calls itself {name}"
            : $"a web server that answers HTTP {status.ToString(CultureInfo.InvariantCulture)}");
    }

    public static LocalPortAnswer Classify(HttpRequestException ex) {
        var error = ex.HttpRequestError != HttpRequestError.Unknown
            ? ex.HttpRequestError
            : (ex.InnerException as HttpIOException)?.HttpRequestError ?? HttpRequestError.Unknown;

        if (error == HttpRequestError.InvalidResponse) return new LocalPortAnswer.Foreign("a program that does not speak HTTP");

        return ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
            ? LocalPortAnswer.Refused.Instance
            : new LocalPortAnswer.Silent(ex.Message);
    }

    /// <summary>The server's report is a few hundred bytes; past the cap it is not the server's, and is not read.</summary>
    static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct) {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBodyBytes + 1];
        var read   = 0;

        while (read < buffer.Length) {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }

        return read > MaxBodyBytes ? "" : Encoding.UTF8.GetString(buffer, 0, read);
    }
}
