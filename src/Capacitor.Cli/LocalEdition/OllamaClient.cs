using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.LocalEdition;

/// <summary>The calls local setup makes to an Ollama on this machine. The local server reaches the same Ollama at
/// <see cref="DefaultUrl"/>, which is why setup looks nowhere else.</summary>
sealed class OllamaClient(HttpClient http, Uri baseUrl, TimeProvider time) {
    public const string DefaultModel = "mxbai-embed-large";

    public static readonly Uri DefaultUrl = new("http://localhost:11434");

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a pull may go without a line before it counts as stalled.</summary>
    static readonly TimeSpan PullIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The first embed loads the model into memory.</summary>
    static readonly TimeSpan EmbedTimeout = TimeSpan.FromMinutes(2);

    public Uri BaseUrl => baseUrl;

    /// <summary>Ollama's version, or null when nothing answers there as Ollama.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct) {
        try {
            using var bounded  = Bounded(ct, ProbeTimeout);
            using var response = await http.GetAsync(Endpoint("api/version"), bounded.Token);

            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadFromJsonAsync(OllamaWire.Context.Default.VersionResponse, bounded.Token);

            return body?.Version is { Length: > 0 } version ? version : null;
        } catch (Exception ex) when (Failed(ex, ct)) {
            return null;
        }
    }

    /// <summary>Whether Ollama already has <paramref name="model"/>; an untagged name means its <c>latest</c>.</summary>
    public async Task<bool> HasModelAsync(string model, CancellationToken ct) {
        try {
            using var bounded  = Bounded(ct, ProbeTimeout);
            using var response = await http.GetAsync(Endpoint("api/tags"), bounded.Token);

            if (!response.IsSuccessStatusCode) throw new OllamaException($"listing its models answered HTTP {(int)response.StatusCode}");

            var body  = await response.Content.ReadFromJsonAsync(OllamaWire.Context.Default.TagsResponse, bounded.Token);
            var names = body?.Models?.SelectMany(m => new[] { m?.Name, m?.Model }).OfType<string>() ?? [];
            var tagged = model.Contains(':') ? model : $"{model}:latest";

            return names.Any(n => n == model || n == tagged);
        } catch (Exception ex) when (Failed(ex, ct)) {
            throw new OllamaException(ex.Message, ex);
        }
    }

    /// <summary>Pulls <paramref name="model"/>, reporting bytes received and expected over every layer seen so far.
    /// Ends only on Ollama's own <c>success</c>.</summary>
    public async Task PullAsync(string model, IProgress<(long Received, long Total)> progress, CancellationToken ct) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint("api/pull")) {
                Content = JsonContent.Create(new OllamaWire.PullRequest { Model = model }, OllamaWire.Context.Default.PullRequest),
            };

            HttpResponseMessage response;

            using (var headers = Bounded(ct, PullIdleTimeout))
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);

            using var _ = response;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var layers = new Dictionary<string, (long Received, long Total)>(StringComparer.Ordinal);

            while (true) {
                string? line;

                using (var idle = Bounded(ct, PullIdleTimeout))
                    line = await reader.ReadLineAsync(idle.Token);

                if (line is null) break;
                if (line.Length == 0) continue;

                var status = JsonSerializer.Deserialize(line, OllamaWire.Context.Default.PullStatus);

                if (status?.Error is { Length: > 0 } error) throw new OllamaException(error);
                if (status?.Status == "success") return;

                if (status is { Digest: { Length: > 0 } digest, Total: long total and > 0 }) {
                    layers[digest] = (status.Completed ?? 0, total);
                    progress.Report((layers.Values.Sum(l => l.Received), layers.Values.Sum(l => l.Total)));
                }
            }

            throw new OllamaException(response.IsSuccessStatusCode
                ? "the pull ended before Ollama reported success"
                : $"the pull answered HTTP {(int)response.StatusCode}");
        } catch (Exception ex) when (Failed(ex, ct)) {
            throw new OllamaException(ex is OperationCanceledException ? $"no progress for {PullIdleTimeout.TotalMinutes:0} minutes" : ex.Message, ex);
        }
    }

    /// <summary>Embeds one string the way the local server does, and returns the vector's width. A model that cannot
    /// embed fails here rather than on the server's first search.</summary>
    public async Task<int> EmbedProbeAsync(string model, CancellationToken ct) {
        try {
            using var bounded = Bounded(ct, EmbedTimeout);
            var body = new OllamaWire.EmbedRequest { Model = model, Input = ["kcap"], Truncate = false };

            using var response = await http.PostAsJsonAsync(Endpoint("api/embed"), body, OllamaWire.Context.Default.EmbedRequest, bounded.Token);

            var text = await response.Content.ReadAsStringAsync(bounded.Token);

            if (!response.IsSuccessStatusCode)
                throw new OllamaException(ErrorIn(text) ?? $"embedding answered HTTP {(int)response.StatusCode}");

            var answer = JsonSerializer.Deserialize(text, OllamaWire.Context.Default.EmbedResponse);

            if (answer?.Error is { Length: > 0 } error) throw new OllamaException(error);

            return answer?.Embeddings is [{ Length: > 0 } vector]
                ? vector.Length
                : throw new OllamaException("embedding returned no vector");
        } catch (Exception ex) when (Failed(ex, ct)) {
            throw new OllamaException(ex is OperationCanceledException ? $"embedding took longer than {EmbedTimeout.TotalMinutes:0} minutes" : ex.Message, ex);
        }
    }

    Uri Endpoint(string path) => new(baseUrl, path);

    Deadline Bounded(CancellationToken ct, TimeSpan timeout) => new(ct, timeout, time);

    /// <summary>The caller's token, cancelled as well once the timeout passes.</summary>
    sealed class Deadline : IDisposable {
        readonly CancellationTokenSource _timer;
        readonly CancellationTokenSource _linked;

        public Deadline(CancellationToken ct, TimeSpan timeout, TimeProvider time) {
            _timer  = new(timeout, time);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose() {
            _linked.Dispose();
            _timer.Dispose();
        }
    }

    static string? ErrorIn(string text) {
        try {
            return JsonSerializer.Deserialize(text, OllamaWire.Context.Default.ErrorResponse)?.Error is { Length: > 0 } error ? error : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>A failure of Ollama or the connection to it, as opposed to the caller's own cancellation.</summary>
    static bool Failed(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or NotSupportedException or IOException
     || ex is OperationCanceledException && !ct.IsCancellationRequested;
}
