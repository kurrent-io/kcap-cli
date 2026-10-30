using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Who answers on the local server's port, told from the answer alone: no process lookup, so it reads the
/// same on every platform and for another user's process.</summary>
public static class LocalPortProbe {
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    const int MaxNameLength = 80;

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
            return Classify(response);
        } catch (HttpRequestException ex) {
            return Classify(ex);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new LocalPortAnswer.Silent($"no answer within {RequestTimeout.TotalSeconds:0} seconds");
        }
    }

    /// <summary>The kcap server's health route answers 200 when ready and 503 when not; any server can fail with
    /// another 5xx. Every other status is another program's.</summary>
    public static LocalPortAnswer Classify(HttpResponseMessage response) {
        var status = (int)response.StatusCode;
        if (status == (int)HttpStatusCode.OK || status >= 500) return LocalPortAnswer.Answered.Instance;

        var name = response.Headers.TryGetValues("Server", out var values) ? string.Join(' ', values).Trim() : "";
        if (name.Length > MaxNameLength) name = name[..MaxNameLength] + "…";

        return new LocalPortAnswer.Foreign(name.Length > 0
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
}
