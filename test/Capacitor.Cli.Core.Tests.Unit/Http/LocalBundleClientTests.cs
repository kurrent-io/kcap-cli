using System.Net;
using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

public class LocalBundleClientTests {
    [Test, NotInParallel]
    public async Task Urls_follow_the_publication_layout() {
        using var _ = EnvScope.Exclusive(LocalBundleClient.BaseUrlEnvVar, null);
        var urls = new List<string>();
        var client = Client(r => { urls.Add(r.RequestUri!.ToString()); return Reply(HttpStatusCode.OK, "x"u8.ToArray()); });

        await client.FetchManifestAsync("1.3.0", "osx-arm64", CancellationToken.None);

        await Assert.That(urls).IsEquivalentTo([
            "https://www.kurrent.io/download/local/osx-arm64/kcap-local-1.3.0-osx-arm64.json",
            "https://www.kurrent.io/download/local/osx-arm64/kcap-local-1.3.0-osx-arm64.json.sig"
        ]);
    }

    [Test]
    public async Task A_missing_manifest_names_the_overrides() {
        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.NotFound)).FetchManifestAsync("1.3.0", "osx-arm64", CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.BundleNotPublished);
        await Assert.That(e.Message).Contains("KCAP_LOCAL_BUNDLE_VERSION");
        await Assert.That(e.Message).Contains("KCAP_LOCAL_BUNDLE_BASE_URL");
    }

    [Test]
    public async Task A_missing_signature_is_a_download_failure() {
        var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
            ? Reply(HttpStatusCode.NotFound)
            : Reply(HttpStatusCode.OK, "x"u8.ToArray()));

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await client.FetchManifestAsync("1.3.0", "osx-arm64", CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.DownloadFailed);
    }

    [Test]
    public async Task A_server_error_is_a_download_failure() {
        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.InternalServerError)).FetchManifestAsync("1.3.0", "osx-arm64", CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.DownloadFailed);
    }

    [Test]
    public async Task A_network_error_is_a_download_failure() {
        var client = new LocalBundleClient(new(new Handler(_ => throw new HttpRequestException("down"))), TimeProvider.System);

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await client.FetchManifestAsync("1.3.0", "osx-arm64", CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.DownloadFailed);
    }

    [Test]
    public async Task A_caller_cancellation_propagates() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK, "x"u8.ToArray())).FetchManifestAsync("1.3.0", "osx-arm64", cts.Token));
    }

    [Test]
    [Arguments("1.3.0/../x", "osx-arm64")]
    [Arguments("1.3.0", "../osx")]
    [Arguments("1.3.0", "OSX")]
    public async Task Unsafe_url_parts_are_refused(string version, string rid) {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK)).FetchManifestAsync(version, rid, CancellationToken.None));
    }

    [Test, NotInParallel]
    public async Task The_base_url_override_is_honoured() {
        using var _ = EnvScope.Exclusive(LocalBundleClient.BaseUrlEnvVar, "https://mirror.test/x/");
        var url = "";
        var client = Client(r => { url = r.RequestUri!.ToString(); return Reply(HttpStatusCode.OK, "x"u8.ToArray()); });

        await client.FetchManifestAsync("1.3.0", "linux-x64", CancellationToken.None);

        await Assert.That(url).StartsWith("https://mirror.test/x/linux-x64/kcap-local-1.3.0-linux-x64.json");
    }

    [Test, NotInParallel]
    public async Task The_requested_version_is_read_at_call_time() {
        using var a = EnvScope.Exclusive(LocalBundleClient.VersionEnvVar, "9.9.9");
        await Assert.That(LocalBundleClient.RequestedVersion()).IsEqualTo("9.9.9");
    }

    [Test]
    public async Task A_good_download_lands_on_disk_and_reports_progress() {
        using var tmp = new TempDir();
        var body = RandomNumberGenerator.GetBytes(2_500_000);
        var dest = tmp.PathTo("a.tar.gz");
        var seen = new List<long>();

        await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(ManifestFor(body), dest, new Collect(seen), CancellationToken.None);

        await Assert.That(File.ReadAllBytes(dest).SequenceEqual(body)).IsTrue();
        await Assert.That(seen.Last()).IsEqualTo(body.Length);
        await Assert.That(seen.Count).IsLessThanOrEqualTo(3);
    }

    [Test]
    public async Task A_last_step_report_at_the_total_is_not_repeated() {
        using var tmp = new TempDir();
        var body = RandomNumberGenerator.GetBytes(1024 * 1024);
        var seen = new List<long>();

        await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(ManifestFor(body), tmp.PathTo("a.tar.gz"), new Collect(seen), CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo([(long)body.Length]);
    }

    [Test]
    public async Task An_oversize_body_stops_at_the_declared_size() {
        using var tmp = new TempDir();
        var body = new byte[100];
        var m    = ManifestFor(body) with { Size = 50 };

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(m, tmp.PathTo("a"), null, CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
    }

    [Test]
    public async Task A_short_body_is_refused() {
        using var tmp = new TempDir();
        var body = new byte[100];
        var m    = ManifestFor(body) with { Size = 200 };

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(m, tmp.PathTo("a"), null, CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
    }

    [Test]
    public async Task A_digest_mismatch_is_refused() {
        using var tmp = new TempDir();
        var body = new byte[100];
        var m    = ManifestFor(body) with { Sha256 = new string('0', 64) };

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(m, tmp.PathTo("a"), null, CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.VerificationFailed);
    }

    [Test]
    public async Task A_failed_archive_request_is_a_download_failure() {
        using var tmp = new TempDir();

        var e = await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.NotFound)).DownloadAsync(ManifestFor(new byte[1]), tmp.PathTo("a"), null, CancellationToken.None));

        await Assert.That(e!.Code).IsEqualTo(LocalInstallFailureCode.DownloadFailed);
    }

    [Test]
    public async Task An_existing_destination_is_never_overwritten() {
        using var tmp = new TempDir();
        var dest = tmp.CreateFile("a", "keep");
        var body = new byte[10];

        await Assert.ThrowsAsync<LocalBundleException>(async () =>
            await Client(_ => Reply(HttpStatusCode.OK, body)).DownloadAsync(ManifestFor(body), dest, null, CancellationToken.None));

        await Assert.That(File.ReadAllText(dest)).IsEqualTo("keep");
    }

    [Test]
    public async Task A_model_download_that_stops_receiving_bytes_fails_once_the_stall_timeout_passes() {
        using var tmp    = new TempDir();
        var       time   = new FakeTimeProvider();
        var       body   = new StallingStream(new byte[1000]);
        var       client = new LocalBundleClient(new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) })), time);

        var download = client.DownloadModelFileAsync(new string('b', 64), new("model.onnx", new string('a', 64), 2000), tmp.PathTo("m"), null, CancellationToken.None);
        await body.Stalled;

        time.Advance(client.ModelStallTimeout - TimeSpan.FromSeconds(1));
        await Assert.That(download.IsCompleted).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        var e = await Assert.ThrowsAsync<LocalModelDownloadException>(async () => await download.WaitAsync(TimeSpan.FromSeconds(30)));

        await Assert.That(e!.Code).IsEqualTo(LocalModelDownloadCode.DownloadFailed);
    }

    [Test]
    public async Task A_slow_but_steady_model_download_outlasts_the_bundle_deadline() {
        using var tmp    = new TempDir();
        var       time   = new FakeTimeProvider();
        var       data   = RandomNumberGenerator.GetBytes(40_000);
        var       client = new LocalBundleClient(new(new Handler(_ => new(HttpStatusCode.OK) {
            Content = new StreamContent(new TricklingStream(data, 1000, () => time.Advance(TimeSpan.FromSeconds(30))))
        })), time);
        var dest  = tmp.PathTo("m");
        var start = time.GetUtcNow();

        await client.DownloadModelFileAsync(new string('b', 64), new("model.onnx", Convert.ToHexStringLower(SHA256.HashData(data)), data.Length), dest, null, CancellationToken.None);

        await Assert.That(time.GetUtcNow() - start).IsGreaterThan(client.DownloadTimeout);
        await Assert.That(File.ReadAllBytes(dest).SequenceEqual(data)).IsTrue();
    }

    static LocalBundleManifest ManifestFor(byte[] body) =>
        new("1.3.0", "osx-arm64", "kcap-local-1.3.0-osx-arm64.tar.gz",
            Convert.ToHexStringLower(SHA256.HashData(body)), body.Length, new string('c', 64));

    static LocalBundleClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new(new Handler(respond)), TimeProvider.System);

    static HttpResponseMessage Reply(HttpStatusCode status, byte[]? body = null) =>
        new(status) { Content = new ByteArrayContent(body ?? []) };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    sealed class Collect(List<long> into) : IProgress<long> {
        public void Report(long value) => into.Add(value);
    }

    /// <summary>Yields <paramref name="first"/> once, then never yields again until cancelled.</summary>
    sealed class StallingStream(byte[] first) : ReadOnlyBody {
        readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool _served;

        public Task Stalled => _stalled.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            if (!_served) {
                _served = true;
                first.CopyTo(buffer);
                return first.Length;
            }

            _stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    /// <summary>Yields <paramref name="data"/> <paramref name="chunk"/> bytes per read, calling <paramref name="beforeRead"/> first.</summary>
    sealed class TricklingStream(byte[] data, int chunk, Action beforeRead) : ReadOnlyBody {
        int _offset;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            beforeRead();
            var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _offset);
            data.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return ValueTask.FromResult(n);
        }
    }

    abstract class ReadOnlyBody : Stream {
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
