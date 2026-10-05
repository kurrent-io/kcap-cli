using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>
/// Client for the Grok Bot cloud computer's local gateway, an undocumented internal API: every call is
/// <c>POST /api/{method}</c> with the bearer token from <c>gateway.json</c>. The descriptor is re-read on
/// every call, because the gateway restarts with a new port and token.
/// </summary>
public sealed class GrokBotGateway(string descriptorPath, HttpClient http) {
    public const int TailLimit = 200;

    // Fields that move when a chat gains or changes an entry. lastEntry and localActivityAt look as if they
    // should and do not: one previews how the last message opens, the other is the chat's creation time.
    static readonly string[] ChangeFields =
        ["newestEntryId", "snapshotSeq", "updatedAt", "lastActivityAt", "lastMessageId", "isRunningTurn", "awaitingUserResponse"];

    public bool Available => File.Exists(descriptorPath);

    public async Task<IReadOnlyList<GrokBotAgent>> ListAgentsAsync(CancellationToken ct) {
        using var doc = await CallAsync("listAgents", new JsonObject(), ct);

        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                 : doc.RootElement.TryGetProperty("agents", out var agents) ? agents
                 : default;
        if (list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<GrokBotAgent>();
        foreach (var a in list.EnumerateArray()) {
            if (!a.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } agentId) continue;

            var fingerprint = string.Join("|", ChangeFields.Select(f => a.TryGetProperty(f, out var v) ? v.GetRawText() : ""));

            result.Add(new GrokBotAgent(
                agentId,
                a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "",
                a.TryGetProperty("isRunningTurn", out var r) && r.ValueKind == JsonValueKind.True,
                fingerprint));
        }

        return result;
    }

    /// <summary>The newest entries, or the page before <paramref name="beforeSeq"/>. Returns the
    /// parsed entries and the cursor for the page before them, when the gateway gives one.</summary>
    public async Task<(IReadOnlyList<GrokBotEntry> Entries, int? NextBeforeSeq)> TailAsync(
            string agentId, int? beforeSeq, CancellationToken ct) {
        var body = new JsonObject { ["id"] = agentId, ["limit"] = TailLimit };
        if (beforeSeq is { } before) body["beforeSeq"] = before;

        using var doc = await CallAsync("getAgentTranscriptTail", body, ct);
        var root = doc.RootElement;

        var entries = new List<GrokBotEntry>();
        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array) {
            foreach (var e in list.EnumerateArray())
                if (GrokBotEntry.TryParse(e, agentId) is { } entry) entries.Add(entry);
        }

        int? next = root.TryGetProperty("nextBeforeSeq", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var nv) ? nv : null;
        return (entries, next);
    }

    async Task<JsonDocument> CallAsync(string method, JsonObject body, CancellationToken ct) {
        var (baseUrl, token) = ReadDescriptor();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/{method}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"gateway {method} answered {(int)response.StatusCode}", null, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    (string BaseUrl, string Token) ReadDescriptor() {
        var root   = JsonNode.Parse(File.ReadAllText(descriptorPath))!.AsObject();
        var scheme = root["scheme"]?.GetValue<string>() is { Length: > 0 } s ? s : "http";
        var host   = root["host"]?.GetValue<string>() is { Length: > 0 } h && h != "0.0.0.0" ? h : "127.0.0.1";
        var port   = root["port"]?.GetValue<int>() ?? throw new InvalidDataException("gateway.json has no port");
        var token  = root["token"]?.GetValue<string>() ?? throw new InvalidDataException("gateway.json has no token");

        return ($"{scheme}://{host}:{port}", token);
    }
}
