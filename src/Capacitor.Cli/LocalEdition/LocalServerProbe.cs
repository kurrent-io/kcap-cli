using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.LocalEdition;

static class LocalServerProbe {
    public const string PendingHeader = "Kcap-Installation";

    /// <summary>Never throws except <see cref="OperationCanceledException"/> from <paramref name="ct"/>. The client
    /// must not follow redirects.</summary>
    public static async Task<AuthConfigAnswer> AuthConfigAsync(HttpClient http, string baseUrl, CancellationToken ct) {
        try {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/auth/config", ct);

            if (response.StatusCode != HttpStatusCode.OK) return AuthConfigAnswer.Other;

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            if (!string.Equals(doc.RootElement.Str("provider"), "None", StringComparison.Ordinal))
                return AuthConfigAnswer.Other;

            if (!response.Headers.TryGetValues(PendingHeader, out var values)) return AuthConfigAnswer.Lifted;

            return values.SingleOrDefault() == "pending" ? AuthConfigAnswer.Pending : AuthConfigAnswer.Other;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception) {
            return AuthConfigAnswer.Other;
        }
    }

    static readonly TimeSpan EmbeddingsTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Null for an older server's 404, a failed or silent request, or a body that is not the report. Never
    /// throws except <see cref="OperationCanceledException"/> from <paramref name="ct"/>.</summary>
    public static async Task<EmbeddingHealthAnswer?> EmbeddingsAsync(HttpClient http, string baseUrl, TimeProvider time, CancellationToken ct) {
        using var attempt = new CancellationTokenSource(EmbeddingsTimeout, time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Token);

        try {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/healthz/embeddings", linked.Token);

            if (response.StatusCode != HttpStatusCode.OK) return null;

            using var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync(linked.Token));
            var       root = doc.RootElement;

            if (root.Str("provider") is not { } provider || root.Bool("usable") is not { } usable) return null;

            return new(provider, usable, root.Str("reason"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception) {
            return null;
        }
    }
}
