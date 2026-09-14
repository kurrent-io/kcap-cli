using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class EntitiesApi(ICapacitorHttpClient http, CapacitorServer server) : IEntitiesApi {
    public Task<EntityRegistryResult> GetAsync(string repoHash, CancellationToken ct = default) =>
        ReadAsync((c, token) => c.GetWithRetryAsync(
            $"{server.Url}/api/work-items/entities?repo={Uri.EscapeDataString(repoHash)}", ct: token), ct);

    public Task<EntityRegistryResult> RegisterAsync(string repoHash, string value, CancellationToken ct = default) =>
        ReadAsync((c, token) => c.PostAsJsonAsync(
            $"{server.Url}/api/work-items/entities",
            new CliRegisterEntityRequest { RepoHash = repoHash, Value = value },
            CapacitorJsonContext.Default.CliRegisterEntityRequest, token), ct);

    public Task<EntityRegistryResult> WithdrawAsync(string repoHash, string value, CancellationToken ct = default) =>
        ReadAsync((c, token) => c.PostAsJsonAsync(
            $"{server.Url}/api/work-items/entities/withdraw",
            new CliWithdrawEntityRequest { RepoHash = repoHash, Value = value },
            CapacitorJsonContext.Default.CliWithdrawEntityRequest, token), ct);

    async Task<EntityRegistryResult> ReadAsync(
            Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) {
        // PostAsJsonAsync does not check this for itself, and a relative URL throws from inside HttpClient.
        if (!HttpClientExtensions.IsAcceptableUrl(server.Url))
            throw new CapacitorApiException(null, $"Server URL is not usable: '{server.Url}'.");

        using var response = await CapacitorApiRequests.SendAsync(http, server, send, ct);

        if (response.IsSuccessStatusCode) {
            var registry = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.CliEntityRegistry, ct);

            return registry is null ? new EntityRegistryResult.NotFound() : new EntityRegistryResult.Found(registry);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)   return new EntityRegistryResult.NotFound();
        if (response.StatusCode == HttpStatusCode.BadRequest) return new EntityRegistryResult.Rejected(await DetailAsync(response, ct));
        if (response.StatusCode == HttpStatusCode.Forbidden)  return new EntityRegistryResult.Forbidden(await ErrorCodeAsync(response, ct));

        throw await CapacitorApiRequests.FailureAsync(response);
    }

    /// <summary>A problem document's <c>detail</c>, falling back to the raw body: the server says
    /// what it refused, and echoing that beats restating its rules here where they would drift.</summary>
    static async Task<string> DetailAsync(HttpResponseMessage response, CancellationToken ct) {
        var body = await response.Content.ReadAsStringAsync(ct);

        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.Str("detail") ?? body;
        } catch (JsonException) {
            return body;
        }
    }

    static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return doc.RootElement.Str("error");
        } catch (JsonException) {
            return null;
        }
    }
}
