using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Reads the script installer's manifests from kcap-web and downloads release archives from GitHub.
/// The same two hosts, and the same overrides, as the install scripts. Carries neither our credential
/// nor our observation headers.
/// </summary>
public sealed class KcapReleaseClient(HttpClient http, TimeProvider time) : IReleaseFeed {
    public const string BaseUrlEnvVar     = "KCAP_INSTALL_BASE_URL";
    public const string ReleasesUrlEnvVar = "KCAP_INSTALL_RELEASES_URL";

    static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Covers the body as well as the headers: a transfer that stalls after them would
    /// otherwise hang <c>kcap update</c>, since the client's own timeout ends at the headers.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public string BaseUrl { get; init; } =
        (Environment.GetEnvironmentVariable(BaseUrlEnvVar) is { Length: > 0 } b ? b : "https://www.kurrent.io").TrimEnd('/');

    public string ReleasesUrl { get; init; } =
        (Environment.GetEnvironmentVariable(ReleasesUrlEnvVar) is { Length: > 0 } r ? r : "https://github.com/kurrent-io/kcap-cli/releases/download").TrimEnd('/');

    public string ChannelManifestUrl(string channel) => $"{BaseUrl}/download/cli/channels/{channel}.json";

    public string VersionManifestUrl(string version) => $"{BaseUrl}/download/cli/{version}/manifest.json";

    public string ArchiveUrl(string version, string rid) =>
        $"{ReleasesUrl}/v{version}/kcap-{rid}{(rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz")}";

    public async Task<NpmDistTag> GetDistTagAsync(string channel, CancellationToken ct) {
        var (reached, manifest) = await GetManifestAsync(ChannelManifestUrl(channel), ct);

        return new(reached, manifest?.Version);
    }

    /// <summary>The manifest of one release, or null when it could not be fetched or read.</summary>
    public async Task<ReleaseManifest?> GetVersionManifestAsync(string version, CancellationToken ct) =>
        (await GetManifestAsync(VersionManifestUrl(version), ct)).Manifest;

    async Task<(bool Reached, ReleaseManifest? Manifest)> GetManifestAsync(string url, CancellationToken ct) {
        try {
            using var deadline = new CancellationTokenSource(ManifestTimeout, time);
            using var cts      = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var resp = await http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return (false, null);

            var manifest = ReleaseManifest.Parse(await resp.Content.ReadAsStringAsync(cts.Token));

            return (manifest is not null, manifest);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return (false, null);
        }
    }

    /// <summary>Streams <paramref name="url"/> into <paramref name="destination"/>. Throws on any failure.</summary>
    public async Task DownloadAsync(string url, string destination, CancellationToken ct) {
        using var deadline = new CancellationTokenSource(DownloadTimeout, time);
        using var cts      = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{url} answered {(int)resp.StatusCode}.");

        await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await body.CopyToAsync(file, cts.Token);
    }
}
