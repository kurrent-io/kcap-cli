using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Fetches the local-edition server bundle, its manifest and the manifest's signature from the
/// publication bucket. Carries neither our credential nor our observation headers.
/// </summary>
public sealed partial class LocalBundleClient(HttpClient http, TimeProvider time) {
    public const string BaseUrlEnvVar   = "KCAP_LOCAL_BUNDLE_BASE_URL";
    public const string VersionEnvVar   = "KCAP_LOCAL_BUNDLE_VERSION";
    const long          ProgressStep    = 1024 * 1024;
    const int           MaxManifestSize = 64 * 1024;

    static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Covers the body as well as the headers, which the client's own timeout does not.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>A model file has no whole-transfer deadline, only this limit on a silence between reads: its size
    /// would make any fixed deadline fail a slow link. A minute outlasts TCP's retransmission backoff on a lossy link.</summary>
    public TimeSpan ModelStallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public string BaseUrl { get; init; } =
        (Environment.GetEnvironmentVariable(BaseUrlEnvVar) is { Length: > 0 } b ? b : "https://www.kurrent.io/download/local").TrimEnd('/');

    public static string RequestedVersion() =>
        Environment.GetEnvironmentVariable(VersionEnvVar) is { Length: > 0 } v ? v : CapacitorVersion.CurrentDisplay();

    public async Task<(byte[] Manifest, string Signature)> FetchManifestAsync(string version, string rid, CancellationToken ct) {
        if (!ReleaseManifest.IsValidVersion(version)) throw new ArgumentException("Not a release version.", nameof(version));
        if (!RidPattern().IsMatch(rid)) throw new ArgumentException("Not a runtime identifier.", nameof(rid));

        var url = $"{BaseUrl}/{rid}/kcap-local-{version}-{rid}.json";

        var manifest = await GetBytesAsync(url, notFound: LocalInstallFailureCode.BundleNotPublished, ct, MaxManifestSize);
        var sig      = await GetBytesAsync(url + ".sig", notFound: LocalInstallFailureCode.DownloadFailed, ct, MaxManifestSize);

        return (manifest, System.Text.Encoding.UTF8.GetString(sig));
    }

    async Task<byte[]> GetBytesAsync(string url, LocalInstallFailureCode notFound, CancellationToken ct, int limit) {
        try {
            using var deadline = new CancellationTokenSource(ManifestTimeout, time);
            using var cts      = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var resp     = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
                throw notFound == LocalInstallFailureCode.BundleNotPublished
                    ? new LocalBundleException(notFound,
                        $"No local bundle is published at {url}. Set {VersionEnvVar} to a published version, or {BaseUrlEnvVar} to another location.")
                    : new LocalBundleException(notFound, $"{url} answered 404.");
            }

            if (!resp.IsSuccessStatusCode)
                throw new LocalBundleException(LocalInstallFailureCode.DownloadFailed, $"{url} answered {(int)resp.StatusCode}.");

            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var ms         = new MemoryStream();
            var       buffer     = new byte[8192];
            int       n;

            while ((n = await body.ReadAsync(buffer, cts.Token)) > 0) {
                ms.Write(buffer, 0, n);
                if (ms.Length > limit)
                    throw new LocalBundleException(LocalInstallFailureCode.DownloadFailed, $"{url} is larger than expected.");
            }

            return ms.ToArray();
        } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested) {
            throw new LocalBundleException(LocalInstallFailureCode.DownloadFailed, $"Could not read {url}: {e.Message}", e);
        }
    }

    /// <summary>Streams the archive named by <paramref name="m"/> into a new file, refusing more bytes
    /// than the manifest declares and a final size or digest that differs from it.</summary>
    public async Task DownloadAsync(LocalBundleManifest m, string destination, IProgress<long>? progress, CancellationToken ct) {
        if (!ReleaseManifest.IsValidVersion(m.Version)) throw new ArgumentException("Not a release version.", nameof(m));
        if (!RidPattern().IsMatch(m.Rid)) throw new ArgumentException("Not a runtime identifier.", nameof(m));

        var url = $"{BaseUrl}/{m.Rid}/{m.Archive}";

        try {
            using var deadline = new CancellationTokenSource(DownloadTimeout, time);
            using var cts      = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var resp     = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
                throw new LocalBundleException(LocalInstallFailureCode.DownloadFailed, $"{url} answered {(int)resp.StatusCode}.");

            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var       hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var  buffer   = new byte[81920];
            long total    = 0;
            long reported = 0;
            int  n;

            while ((n = await body.ReadAsync(buffer, cts.Token)) > 0) {
                total += n;
                if (total > m.Size)
                    throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, "The bundle is larger than its manifest declares.");

                hash.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), cts.Token);

                if (progress is not null && total - reported >= ProgressStep) {
                    reported = total;
                    progress.Report(total);
                }
            }

            await file.FlushAsync(cts.Token);
            file.Flush(flushToDisk: true);

            if (total != m.Size)
                throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, "The bundle is smaller than its manifest declares.");

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), m.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LocalBundleException(LocalInstallFailureCode.VerificationFailed, "The bundle's checksum does not match its manifest.");

            if (reported != total) progress?.Report(total);
        } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested) {
            throw new LocalBundleException(LocalInstallFailureCode.DownloadFailed, $"Could not download {url}: {e.Message}", e);
        }
    }

    /// <summary>Streams one file of a model set into a new file, refusing more bytes than declared and a final
    /// size or digest that differs. Failures other than the caller's cancellation surface as
    /// <see cref="LocalModelDownloadException"/>, a stall included.</summary>
    public async Task DownloadModelFileAsync(string modelSetId, LocalEmbeddingModelFile file, string destination, IProgress<long>? progress, CancellationToken ct) {
        if (!ReleaseManifest.IsValidSha256(modelSetId)) throw new ArgumentException("Not a model set id.", nameof(modelSetId));
        if (!LocalEmbeddingModelFile.IsSafeName(file.Name)) throw new ArgumentException("Not a model file name.", nameof(file));

        var url = $"{BaseUrl}/models/{modelSetId}/{file.Name}";

        using var stall = new CancellationTokenSource();
        using var timer = time.CreateTimer(CancelStalled, stall, ModelStallTimeout, Timeout.InfiniteTimeSpan);

        try {
            using var cts  = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new LocalModelDownloadException(LocalModelDownloadCode.ModelUnavailable, $"{url} answered 404.");

            if (!resp.IsSuccessStatusCode)
                throw new LocalModelDownloadException(LocalModelDownloadCode.DownloadFailed, $"{url} answered {(int)resp.StatusCode}.");

            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var dest = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var       hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var  buffer   = new byte[81920];
            long total    = 0;
            long reported = 0;
            int  n;

            while ((n = await body.ReadAsync(buffer, cts.Token)) > 0) {
                timer.Change(ModelStallTimeout, Timeout.InfiniteTimeSpan);

                total += n;
                if (total > file.Size)
                    throw new LocalModelDownloadException(LocalModelDownloadCode.VerificationFailed, $"{file.Name} is larger than its manifest declares.");

                hash.AppendData(buffer, 0, n);
                await dest.WriteAsync(buffer.AsMemory(0, n), cts.Token);

                if (progress is not null && total - reported >= ProgressStep) {
                    reported = total;
                    progress.Report(total);
                }
            }

            await dest.FlushAsync(cts.Token);
            dest.Flush(flushToDisk: true);

            if (total != file.Size)
                throw new LocalModelDownloadException(LocalModelDownloadCode.VerificationFailed, $"{file.Name} is smaller than its manifest declares.");

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new LocalModelDownloadException(LocalModelDownloadCode.VerificationFailed, $"{file.Name}'s checksum does not match its manifest.");

            if (reported != total) progress?.Report(total);
        } catch (OperationCanceledException e) when (stall.IsCancellationRequested && !ct.IsCancellationRequested) {
            throw new LocalModelDownloadException(LocalModelDownloadCode.DownloadFailed,
                $"Could not download {url}: nothing arrived for {ModelStallTimeout.TotalSeconds:0} seconds.", e);
        } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested) {
            throw new LocalModelDownloadException(LocalModelDownloadCode.DownloadFailed, $"Could not download {url}: {e.Message}", e);
        }
    }

    static void CancelStalled(object? state) {
        try {
            ((CancellationTokenSource)state!).Cancel();
        } catch (ObjectDisposedException) {
            // Disposing the timer does not wait for a callback already running; the download has finished.
        }
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex RidPattern();
}
