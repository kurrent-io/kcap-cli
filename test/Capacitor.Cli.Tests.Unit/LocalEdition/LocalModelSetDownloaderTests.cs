using System.Net;
using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalModelSetDownloaderTests {
    static readonly byte[] Onnx  = RandomNumberGenerator.GetBytes(300_000);
    static readonly byte[] Vocab = RandomNumberGenerator.GetBytes(5_000);

    static readonly LocalEmbeddingModelManifest Manifest = new("m", 1, new('a', 64), [
        new("model.onnx", Convert.ToHexStringLower(SHA256.HashData(Onnx)), Onnx.Length),
        new("vocab.txt", Convert.ToHexStringLower(SHA256.HashData(Vocab)), Vocab.Length)
    ]);

    [Test]
    public async Task Verified_files_land_in_the_model_set_directory() {
        using var tmp  = new TempDir();
        var       seen = new List<(long, long)>();

        await Downloader(tmp, Serve).DownloadAsync(Manifest, new Collect(seen), CancellationToken.None);

        var dir = tmp.PathTo("models", Manifest.ModelSet);
        await Assert.That(File.ReadAllBytes(Path.Combine(dir, "model.onnx")).SequenceEqual(Onnx)).IsTrue();
        await Assert.That(File.ReadAllBytes(Path.Combine(dir, "vocab.txt")).SequenceEqual(Vocab)).IsTrue();
        await Assert.That(Directory.GetDirectories(tmp.PathTo("models")).Length).IsEqualTo(1);
        await Assert.That(seen.Last()).IsEqualTo((Manifest.TotalSize, Manifest.TotalSize));
        await Assert.That(Downloader(tmp, Serve).IsPresent(Manifest)).IsTrue();
    }

    [Test]
    public async Task The_request_path_follows_the_publication_layout() {
        using var tmp  = new TempDir();
        var       urls = new List<string>();

        await Downloader(tmp, r => { urls.Add(r.RequestUri!.AbsolutePath); return Serve(r); }).DownloadAsync(Manifest, new Collect([]), CancellationToken.None);

        await Assert.That(urls).IsEquivalentTo([$"/download/local/models/{Manifest.ModelSet}/model.onnx", $"/download/local/models/{Manifest.ModelSet}/vocab.txt"]);
    }

    [Test]
    public async Task Digest_mismatch_throws_and_leaves_no_file() {
        using var tmp = new TempDir();
        var       bad = Onnx.ToArray();
        bad[0] ^= 1;

        var e = await Assert.ThrowsAsync<LocalModelDownloadException>(async () =>
            await Downloader(tmp, r => r.RequestUri!.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal) ? Reply(HttpStatusCode.OK, bad) : Serve(r))
                .DownloadAsync(Manifest, new Collect([]), CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalModelDownloadCode.VerificationFailed);
        await Assert.That(Files(tmp)).IsEmpty();
    }

    [Test]
    public async Task Missing_file_404_throws_model_unavailable() {
        using var tmp = new TempDir();

        var e = await Assert.ThrowsAsync<LocalModelDownloadException>(async () =>
            await Downloader(tmp, r => r.RequestUri!.AbsolutePath.EndsWith("vocab.txt", StringComparison.Ordinal) ? Reply(HttpStatusCode.NotFound) : Serve(r))
                .DownloadAsync(Manifest, new Collect([]), CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalModelDownloadCode.ModelUnavailable);
        await Assert.That(Files(tmp)).IsEmpty();
    }

    [Test]
    public async Task A_network_error_throws_download_failed() {
        using var tmp = new TempDir();

        var e = await Assert.ThrowsAsync<LocalModelDownloadException>(async () =>
            await Downloader(tmp, _ => throw new HttpRequestException("down")).DownloadAsync(Manifest, new Collect([]), CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalModelDownloadCode.DownloadFailed);
    }

    [Test]
    public async Task Caller_cancellation_propagates_as_OperationCanceled_and_leaves_no_file() {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Downloader(tmp, r => {
                if (r.RequestUri!.AbsolutePath.EndsWith("vocab.txt", StringComparison.Ordinal)) cts.Cancel();
                return Serve(r);
            }).DownloadAsync(Manifest, new Collect([]), cts.Token));

        await Assert.That(Files(tmp)).IsEmpty();
    }

    /// <summary>Cancellation raised by the last progress report, after every file has verified, still installs
    /// nothing and never asks <see cref="LocalModelSetDownloader.Publishing"/>.</summary>
    [Test]
    public async Task Cancellation_after_the_last_file_verifies_publishes_nothing() {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        var asked = false;
        var d     = new LocalModelSetDownloader(new(tmp.Path), Client(Serve)) { Publishing = _ => asked = true };
        var progress = new OnReport(p => {
            if (p.Received == p.Total) cts.Cancel();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await d.DownloadAsync(Manifest, progress, cts.Token));

        await Assert.That(asked).IsFalse();
        await Assert.That(d.Fetched).IsFalse();
        await Assert.That(Files(tmp)).IsEmpty();
    }

    [Test]
    public async Task A_valid_set_already_in_place_is_not_downloaded_again() {
        using var tmp = new TempDir();
        await Downloader(tmp, Serve).DownloadAsync(Manifest, new Collect([]), CancellationToken.None);
        var requests = 0;

        await Downloader(tmp, r => { requests++; return Serve(r); }).DownloadAsync(Manifest, new Collect([]), CancellationToken.None);

        await Assert.That(requests).IsEqualTo(0);
    }

    [Test]
    public async Task An_invalid_set_in_place_is_replaced_and_no_stale_directory_is_left() {
        using var tmp = new TempDir();
        var       d   = Downloader(tmp, Serve);
        await d.DownloadAsync(Manifest, new Collect([]), CancellationToken.None);
        tmp.CreateFile(["models", Manifest.ModelSet, "vocab.txt"], "abc");

        await d.DownloadAsync(Manifest, new Collect([]), CancellationToken.None);

        await Assert.That(d.IsPresent(Manifest)).IsTrue();
        await Assert.That(Files(tmp).Where(f => Path.GetFileName(f).StartsWith('.'))).IsEmpty();
    }

    [Test]
    public async Task IsPresent_rejects_a_truncated_file() {
        using var tmp = new TempDir();
        var       d   = Downloader(tmp, Serve);
        await d.DownloadAsync(Manifest, new Collect([]), CancellationToken.None);

        tmp.CreateFile(["models", Manifest.ModelSet, "model.onnx"], new string('x', Onnx.Length - 1));

        await Assert.That(d.IsPresent(Manifest)).IsFalse();
    }

    [Test]
    public async Task IsPresent_rejects_a_same_size_file_with_another_digest() {
        using var tmp = new TempDir();
        var       d   = Downloader(tmp, Serve);
        await d.DownloadAsync(Manifest, new Collect([]), CancellationToken.None);

        tmp.CreateFile(["models", Manifest.ModelSet, "vocab.txt"], new string('x', Vocab.Length));

        await Assert.That(d.IsPresent(Manifest)).IsFalse();
    }

    [Test]
    public async Task IsPresent_is_false_when_nothing_was_downloaded() {
        using var tmp = new TempDir();

        await Assert.That(Downloader(tmp, Serve).IsPresent(Manifest)).IsFalse();
    }

    static string[] Files(TempDir tmp) =>
        Directory.Exists(tmp.PathTo("models"))
            ? [..Directory.GetFiles(tmp.PathTo("models"), "*", SearchOption.AllDirectories),
               ..Directory.GetDirectories(tmp.PathTo("models"), ".*")]
            : [];

    static HttpResponseMessage Serve(HttpRequestMessage r) =>
        Reply(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.EndsWith("model.onnx", StringComparison.Ordinal) ? Onnx : Vocab);

    static HttpResponseMessage Reply(HttpStatusCode status, byte[]? body = null) =>
        new(status) { Content = new ByteArrayContent(body ?? []) };

    static LocalModelSetDownloader Downloader(TempDir tmp, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new(tmp.Path), Client(respond));

    static LocalBundleClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new(new Handler(respond)), TimeProvider.System) { BaseUrl = "https://example.test/download/local" };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    sealed class Collect(List<(long, long)> into) : IProgress<(long Received, long Total)> {
        public void Report((long Received, long Total) value) => into.Add(value);
    }

    sealed class OnReport(Action<(long Received, long Total)> report) : IProgress<(long Received, long Total)> {
        public void Report((long Received, long Total) value) => report(value);
    }
}
