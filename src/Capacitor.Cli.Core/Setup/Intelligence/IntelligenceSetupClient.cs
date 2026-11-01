using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>Posts the answer to who runs background intelligence. Never throws short of cancellation: setup carries on
/// whatever the server does.</summary>
public static class IntelligenceSetupClient {
    public static string Url(string serverUrl) => $"{serverUrl.TrimEnd('/')}/api/admin/intelligence";

    public static async Task<IntelligenceSetupOutcome> ApplyAsync(
            HttpClient http, string serverUrl, IntelligenceSetupRequest request, CancellationToken ct) {
        try {
            using var response = await http.PostAsJsonAsync(Url(serverUrl), request, CapacitorJsonContext.Default.IntelligenceSetupRequest, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new IntelligenceSetupOutcome.Failed("this server does not offer the background intelligence setup; update it first");

            if (response.StatusCode == HttpStatusCode.BadRequest && Parse(body, CapacitorJsonContext.Default.IntelligenceSetupError) is { Error.Length: > 0 } refusal)
                return new IntelligenceSetupOutcome.Refused(refusal.Error);

            if (!response.IsSuccessStatusCode) return new IntelligenceSetupOutcome.Failed($"HTTP {(int)response.StatusCode}");

            return Parse(body, CapacitorJsonContext.Default.IntelligenceSetupResponse) is { Mode: not null, Resolving: not null } applied
                ? new IntelligenceSetupOutcome.Applied(applied)
                : new IntelligenceSetupOutcome.Failed("the server's answer was unreadable");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new IntelligenceSetupOutcome.Failed(ex.Message);
        }
    }

    static T? Parse<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class {
        try {
            return JsonSerializer.Deserialize(body, type);
        } catch (JsonException) {
            return null;
        }
    }
}
