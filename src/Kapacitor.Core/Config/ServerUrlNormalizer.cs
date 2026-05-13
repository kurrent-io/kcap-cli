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
}
