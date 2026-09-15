using System.Net.Http.Headers;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Downloads a prompt's attachments into one staging directory and publishes it with a single
/// rename, so a batch lands whole or not at all.
internal sealed class AttachmentFetcher(
        IHttpClientFactory http, Func<Task<TokenResolution>> tokens, ILogger logger) {
    const int CopyBuffer = 64 * 1024;

    static readonly string OverCap = $"over the {InputWire.MaxAttachmentBytes / (1024 * 1024)} MB cap";

    const string RootNotADirectory = "attachment root is not a directory";

    /// <param name="destinationRoot"><c>&lt;cwd&gt;/.attached</c> (Worktree) or
    /// <c>store.DirectoryFor(agentId)</c> (DaemonStore).</param>
    public async Task<AttachmentFetch> FetchAsync(
            string destinationRoot, AttachmentPlacement placement,
            IReadOnlyList<string> ids, CancellationToken ct) {
        // Checked before anything is created or deleted: a root that is a link — a repository can
        // commit one at .attached — sends both the staging directory and the sweep's deletions
        // wherever it points. CreateDirectory through an existing link to a directory succeeds
        // silently, so the pre-existing entry is what has to be refused.
        if (IsNotARealDirectory(destinationRoot)) return new(null, null, RootNotADirectory);

        Directory.CreateDirectory(destinationRoot);

        if (IsNotARealDirectory(destinationRoot)) return new(null, null, RootNotADirectory);

        if (placement == AttachmentPlacement.Worktree) {
            var gitignore = Path.Combine(destinationRoot, ".gitignore");

            if (!File.Exists(gitignore)) await File.WriteAllTextAsync(gitignore, "*\n", ct);
        }

        // Sweeping every staging directory here is only safe because one fetch runs per destination
        // root at a time — callers serialise deliveries per agent. The enumeration is inside the try:
        // its MoveNext is where a vanishing or unreadable directory throws, and a failed sweep must
        // not fail the fetch.
        try {
            foreach (var stale in Directory.EnumerateDirectories(destinationRoot, ".pending-*"))
                WorktreeManager.DeleteTreeNoFollow(stale);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Attachment staging cleanup skipped under {Dir}", destinationRoot);
        }

        var batchId   = Guid.NewGuid().ToString("N");
        var staging   = Path.Combine(destinationRoot, ".pending-" + batchId);
        var published = Path.Combine(destinationRoot, batchId);

        Directory.CreateDirectory(staging);

        var paths = new List<string>(ids.Count);
        var batch = new AttachmentBatch(staging, published, paths);

        try {
            var resolution = await tokens();

            foreach (var id in ids) {
                var (fileName, error) = await FetchOneAsync(id, staging, resolution, ct);

                if (error is not null) {
                    batch.Dispose();

                    return new(null, id, error);
                }

                paths.Add(placement == AttachmentPlacement.Worktree
                    ? $"{Path.GetFileName(destinationRoot)}/{batchId}/{fileName}"
                    : Path.Combine(published, fileName!));
            }

            batch.Publish();

            return new(batch, null, null);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            batch.Dispose();

            throw;
        } catch (Exception ex) {
            batch.Dispose();

            return new(null, ids.Count > paths.Count ? ids[paths.Count] : null, ex.Message);
        }
    }

    async Task<(string? FileName, string? Error)> FetchOneAsync(
            string id, string staging, TokenResolution resolution, CancellationToken ct) {
        using var client = http.CreateClient("Attachments");

        if (resolution.Tokens is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolution.Tokens.AccessToken);

        using var response = await client.GetAsync(
            $"/api/attachments/{id}", HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode) return (null, $"server answered {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength is > InputWire.MaxAttachmentBytes) return (null, OverCap);

        var raw = response.Content.Headers.ContentDisposition?.FileNameStar
               ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
               ?? $"attachment-{id[..Math.Min(8, id.Length)]}";
        var fileName = Path.GetFileName(raw);

        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..")
            return (null, "attachment has no usable file name");

        var path = UniquePath(staging, fileName);

        // The partial file stays behind on the over-cap return: the staging directory it sits in is
        // deleted whole by the caller's Dispose, so nothing published ever holds a truncated body.
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[CopyBuffer];
        long written = 0;

        while (true) {
            // One byte past the cap is enough to know it was exceeded, and is all a body that lies
            // about (or omits) its length gets to spend.
            var want = (int)Math.Min(buffer.Length, InputWire.MaxAttachmentBytes + 1 - written);
            var read = await body.ReadAsync(buffer.AsMemory(0, want), ct);

            if (read == 0) break;

            written += read;

            if (written > InputWire.MaxAttachmentBytes) return (null, OverCap);

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return (Path.GetFileName(path), null);
    }

    /// <summary>Whether an entry is present at this path and is anything other than a real directory,
    /// WITHOUT following it: attribute-based, so a link — dangling or not — reads as present.</summary>
    static bool IsNotARealDirectory(string path) {
        try {
            var attributes = File.GetAttributes(path);

            return attributes.HasFlag(FileAttributes.ReparsePoint) || !attributes.HasFlag(FileAttributes.Directory);
        } catch (FileNotFoundException) {
            return false;
        } catch (DirectoryNotFoundException) {
            return false;
        }
    }

    static string UniquePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);

        for (var n = 2; ; n++) {
            path = Path.Combine(directory, $"{stem}-{n}{ext}");

            if (!File.Exists(path)) return path;
        }
    }
}
