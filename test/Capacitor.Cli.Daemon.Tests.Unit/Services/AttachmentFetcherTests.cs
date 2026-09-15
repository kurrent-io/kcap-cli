using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A batch lands whole or not at all, never buffers more than the cap, and never leaves a file behind
/// when it fails.
// Serialized against itself: six stub servers starting at once under the assembly's full width
// starve each other's startup for a minute apiece, one at a time costs seconds.
[NotInParallel(nameof(AttachmentFetcherTests))]
public class AttachmentFetcherTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    sealed class Factory(string baseUrl) : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(baseUrl) };
    }

    sealed class ThrowingHandler(Func<CancellationToken, Exception> fail) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw fail(ct);
    }

    static Task<TokenResolution> NoTokens() =>
        Task.FromResult(new TokenResolution(null, AuthStatus.NotAuthenticated, null, "default"));

    AttachmentFetcher Fetcher() => new(new Factory(_server.Url!), NoTokens, NullLogger.Instance);

    static string Id(int n) => new((char)('a' + n), 32);

    void Serve(string id, byte[] body, string name = "f.png") =>
        _server.Given(Request.Create().WithPath($"/api/attachments/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body)
                .WithHeader("Content-Disposition", $"attachment; filename=\"{name}\""));

    [Test]
    public async Task Success_publishes_one_batch_directory_by_rename_and_leaves_no_staging() {
        Serve(Id(0), [1, 2, 3], "a.png");
        Serve(Id(1), [4], "b.pdf");
        var root = Tmp.PathTo(".attached");
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0), Id(1)], CancellationToken.None);
        await Assert.That(fetch.Batch).IsNotNull();
        await Assert.That(fetch.Batch!.Paths).IsEquivalentTo([
            $".attached/{Path.GetFileName(fetch.Batch.Directory)}/a.png",
            $".attached/{Path.GetFileName(fetch.Batch.Directory)}/b.pdf"
        ]);
        await Assert.That(Directory.GetDirectories(root, ".pending-*")).IsEmpty();
        await Assert.That(File.ReadAllBytes(Path.Combine(fetch.Batch.Directory, "a.png"))).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(File.Exists(Path.Combine(root, ".gitignore"))).IsTrue();
    }

    [Test]
    public async Task Daemon_store_placement_reports_absolute_paths() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo("store");
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.DaemonStore, [Id(0)], CancellationToken.None);
        await Assert.That(Path.IsPathRooted(fetch.Batch!.Paths[0])).IsTrue();
        await Assert.That(File.Exists(Path.Combine(root, ".gitignore"))).IsFalse();
    }

    [Test]
    public async Task A_failed_id_leaves_no_new_file_or_directory_and_earlier_batches_alone() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo(".attached");
        var first = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0), Id(2)], CancellationToken.None);
        await Assert.That(fetch.Batch).IsNull();
        await Assert.That(fetch.FailedId).IsEqualTo(Id(2));
        await Assert.That(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)).IsEquivalentTo(before);
        await Assert.That(Directory.Exists(first.Batch!.Directory)).IsTrue();
    }

    [Test]
    public async Task Oversize_by_header_and_by_chunked_body_are_refused_and_reading_stops_at_the_cap() {
        var declaring = new CountingHandler(16, InputWire.MaxAttachmentBytes + 1);
        var byHeader = await new AttachmentFetcher(new HandlerFactory(declaring), NoTokens, NullLogger.Instance)
            .FetchAsync(Tmp.PathTo("a"), AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(byHeader.Batch).IsNull();
        await Assert.That(byHeader.Error).Contains("over");
        await Assert.That(declaring.BytesRead).IsEqualTo(0);

        var counting = new CountingHandler(InputWire.MaxAttachmentBytes * 3);
        var fetcher = new AttachmentFetcher(new HandlerFactory(counting), NoTokens, NullLogger.Instance);
        var byBody = await fetcher.FetchAsync(Tmp.PathTo("b"), AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(byBody.Batch).IsNull();
        await Assert.That(byBody.FailedId).IsEqualTo(Id(0));
        await Assert.That(byBody.Error).Contains("over");
        await Assert.That(counting.BytesRead).IsLessThanOrEqualTo(InputWire.MaxAttachmentBytes + 1 + 65536);
        await Assert.That(Directory.GetDirectories(Tmp.PathTo("b"))).IsEmpty();
        await Assert.That(Directory.GetFiles(Tmp.PathTo("b"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f))).IsEquivalentTo([".gitignore"]);
    }

    [Test]
    public async Task Empty_disposition_name_is_refused_and_duplicate_names_in_a_batch_are_distinct() {
        Serve(Id(0), [1], "same.txt");
        Serve(Id(1), [2], "same.txt");
        var ok = await Fetcher().FetchAsync(Tmp.PathTo("d"), AttachmentPlacement.Worktree, [Id(0), Id(1)], CancellationToken.None);
        await Assert.That(ok.Batch!.Paths.Select(p => Path.GetFileName(p))).IsEquivalentTo(["same.txt", "same-2.txt"]);
        Serve(Id(3), [1], "..");
        var bad = await Fetcher().FetchAsync(Tmp.PathTo("e"), AttachmentPlacement.Worktree, [Id(3)], CancellationToken.None);
        await Assert.That(bad.Batch).IsNull();
        await Assert.That(bad.FailedId).IsEqualTo(Id(3));

        Serve(Id(4), [1], "foo/");
        var trailing = await Fetcher().FetchAsync(Tmp.PathTo("f"), AttachmentPlacement.Worktree, [Id(4)], CancellationToken.None);
        await Assert.That(trailing.Batch).IsNull();
        await Assert.That(trailing.FailedId).IsEqualTo(Id(4));
    }

    [Test]
    public async Task A_transport_timeout_fails_the_fetch_and_only_the_callers_cancellation_propagates() {
        var timedOut = new AttachmentFetcher(
            new HandlerFactory(new ThrowingHandler(_ => new TaskCanceledException("HttpClient.Timeout elapsing"))),
            NoTokens, NullLogger.Instance);
        var root = Tmp.PathTo("timeout");
        var fetch = await timedOut.FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(fetch.Batch).IsNull();
        await Assert.That(fetch.FailedId).IsEqualTo(Id(0));
        await Assert.That(fetch.Error).IsNotNull();
        await Assert.That(Directory.GetDirectories(root, ".pending-*")).IsEmpty();

        using var cts = new CancellationTokenSource();
        var cancelled = new AttachmentFetcher(
            new HandlerFactory(new ThrowingHandler(_ => {
                cts.Cancel();

                return new OperationCanceledException(cts.Token);
            })),
            NoTokens, NullLogger.Instance);
        var cancelledRoot = Tmp.PathTo("cancelled");
        await Assert.That(async () => await cancelled.FetchAsync(
                cancelledRoot, AttachmentPlacement.Worktree, [Id(0)], cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(Directory.GetDirectories(cancelledRoot, ".pending-*")).IsEmpty();
    }

    [Test]
    public async Task Rollback_deletes_the_published_batch_and_nothing_else() {
        Serve(Id(0), [1]);
        var root = Tmp.PathTo(".attached");
        var a = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        var b = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        b.Batch!.Rollback();
        await Assert.That(Directory.Exists(b.Batch.Directory)).IsFalse();
        await Assert.That(Directory.Exists(a.Batch!.Directory)).IsTrue();
        b.Batch.Rollback();
    }

    /// <summary>A root that is a link is refused without being followed, so neither the staging
    /// directory nor the stale-batch sweep can be steered outside the daemon-owned tree. The link is
    /// pre-existing on purpose: <c>Directory.CreateDirectory</c> through a link to a directory
    /// succeeds silently, so a check that ran only after it would prove nothing.</summary>
    [Test]
    public async Task A_root_that_is_a_link_or_a_file_is_refused_and_its_target_is_untouched() {
        Serve(Id(0), [1]);
        var target = Tmp.CreateDir("elsewhere");
        var stale  = Directory.CreateDirectory(Path.Combine(target, ".pending-" + new string('0', 32))).FullName;
        var link   = Tmp.PathTo(".attached");
        File.CreateSymbolicLink(link, target.Path);

        var viaLink = await Fetcher().FetchAsync(link, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);

        await Assert.That(viaLink.Batch).IsNull();
        await Assert.That(viaLink.Error).Contains("not a directory");
        await Assert.That(Directory.Exists(stale)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(target)).IsEquivalentTo(new[] { stale });

        var file    = Tmp.CreateFile("plain-file");
        var viaFile = await Fetcher().FetchAsync(file, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);

        await Assert.That(viaFile.Batch).IsNull();
        await Assert.That(viaFile.Error).Contains("not a directory");
        await Assert.That(File.ReadAllText(file)).IsEmpty();
    }

    [Test]
    public async Task A_dangling_gitignore_link_is_left_alone_and_its_target_is_never_created() {
        Serve(Id(0), [1]);
        var root    = Tmp.CreateDir(".attached");
        var outside = Tmp.PathTo("outside", "missing");
        File.CreateSymbolicLink(Path.Combine(root, ".gitignore"), outside);

        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);

        await Assert.That(fetch.Batch).IsNotNull();
        await Assert.That(File.Exists(outside)).IsFalse();
        await Assert.That(new FileInfo(Path.Combine(root, ".gitignore")).LinkTarget).IsEqualTo(outside);
    }

    [Test]
    public async Task A_root_that_cannot_be_created_fails_the_fetch_instead_of_throwing() {
        Serve(Id(0), [1]);
        var root = Path.Combine(Tmp.CreateFile("plain-file"), ".attached");

        var fetch = await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);

        await Assert.That(fetch.Batch).IsNull();
        await Assert.That(fetch.FailedId).IsEqualTo(Id(0));
        await Assert.That(fetch.Error).IsNotNull();
    }

    [Test]
    public async Task Stale_staging_directory_is_removed_by_the_next_fetch() {
        Serve(Id(0), [1]);
        var root = Tmp.CreateDir(".attached");
        var stale = Directory.CreateDirectory(Path.Combine(root, ".pending-" + new string('0', 32))).FullName;
        await Fetcher().FetchAsync(root, AttachmentPlacement.Worktree, [Id(0)], CancellationToken.None);
        await Assert.That(Directory.Exists(stale)).IsFalse();
    }
}
