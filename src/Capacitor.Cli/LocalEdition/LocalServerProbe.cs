using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Who answers <c>/auth/config</c> on the installation's port.</summary>
enum AuthConfigAnswer {
    /// <summary>The provider-less local server with its fence lifted.</summary>
    Lifted,

    /// <summary>The same server, still fenced as a pending installation.</summary>
    Pending,

    /// <summary>Anything else, including a failed or refused request.</summary>
    Other
}

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
}
