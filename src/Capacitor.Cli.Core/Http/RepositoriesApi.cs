using System.Net.Http.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class RepositoriesApi(ICapacitorHttpClient http, CapacitorServer server) : IRepositoriesApi {
    public async Task<List<RepoRecapEntry>> GetRecapsAsync(string repoHash, int limit, CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, c => c.GetWithRetryAsync($"{server.Url}/api/repositories/{repoHash}/recaps?limit={limit}"), ct);

        if (!response.IsSuccessStatusCode) throw await CapacitorApiRequests.FailureAsync(response);

        var entries = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListRepoRecapEntry, ct);

        return entries ?? [];
    }
}
