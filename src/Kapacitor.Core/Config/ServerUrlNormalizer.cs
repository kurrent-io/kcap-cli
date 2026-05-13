namespace kapacitor.Config;

/// <summary>
/// Resolves the scheme for user-supplied server URLs. Used on save paths
/// (config set / profile add / setup) so v1-style scheme-less values do
/// not silently land on disk.
/// </summary>
public static class ServerUrlNormalizer {
    public record Result(string Url, string? Warning);

    static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "host.docker.internal"];

    /// <summary>
    /// Returns the input with an inferred scheme: <c>http://</c> for well-known
    /// loopback hosts, <c>https://</c> otherwise. Trims trailing slashes. Pure;
    /// does no I/O. Used as a fallback when probing fails or is skipped.
    /// </summary>
    public static string WithLoopbackDefault(string input) {
        var trimmed = input.TrimEnd('/');

        if (HasScheme(trimmed)) return trimmed;

        var host = ExtractHost(trimmed);
        var scheme = LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{trimmed}";
    }

    static bool HasScheme(string input) =>
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static string ExtractHost(string input) {
        // Strip path
        var pathStart = input.IndexOf('/');
        var hostAndPort = pathStart >= 0 ? input[..pathStart] : input;

        // IPv6 literal (contains "::") — no port to strip in the bare-host form.
        if (hostAndPort.Contains("::")) return hostAndPort;

        // Strip port — but only the last ":N" if N is digits.
        var lastColon = hostAndPort.LastIndexOf(':');
        if (lastColon > 0 && hostAndPort[(lastColon + 1)..].All(char.IsDigit))
            return hostAndPort[..lastColon];

        return hostAndPort;
    }

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Resolves a server URL the user just supplied: applies the loopback default
    /// for scheme-less input and probes the result (or both schemes) via
    /// <c>GET /auth/config</c>. Never throws — on probe failure, returns the
    /// loopback-default URL with a warning so the caller can decide what to do.
    /// </summary>
    public static async Task<Result> NormalizeAsync(
        string                                                  input,
        bool                                                    skipProbe,
        CancellationToken                                       ct,
        Func<string, TimeSpan, CancellationToken, Task<bool>>?  probe = null) {

        probe ??= HttpProbeAsync;
        var trimmed = input.TrimEnd('/');

        if (skipProbe) return new(WithLoopbackDefault(trimmed), null);

        if (HasScheme(trimmed)) {
            return await probe(trimmed, ProbeTimeout, ct)
                ? new(trimmed, null)
                : new(trimmed, $"could not reach {trimmed}. Saved anyway. Verify with 'kapacitor config show'.");
        }

        var httpsCandidate = $"https://{trimmed}";
        if (await probe(httpsCandidate, ProbeTimeout, ct))
            return new(httpsCandidate, null);

        var httpCandidate = $"http://{trimmed}";
        if (await probe(httpCandidate, ProbeTimeout, ct))
            return new(httpCandidate, null);

        var fallback = WithLoopbackDefault(trimmed);
        return new(fallback, $"could not reach {trimmed} on https or http. Saved as {fallback}. Verify with 'kapacitor config show'.");
    }

    static async Task<bool> HttpProbeAsync(string url, TimeSpan timeout, CancellationToken ct) {
        using var http = new HttpClient { Timeout = timeout };

        try {
            using var resp = await http.GetAsync($"{url}/auth/config", ct);
            // Any HTTP response means the server is reachable. We do not require
            // 200 — older servers without /auth/config still count as "up".
            return true;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            return false;
        }
    }
}
