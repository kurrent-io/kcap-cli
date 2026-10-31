using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// What the source itself decides: a missing sign-in reads signed out without a client, a 401
/// retires the client so the next read builds a fresh one, and a 401 on either read ends every
/// cached answer. No network.
public class ServerPlanArtifactSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";

    /// Answers each request with the next status, and the pages route with one listed page.
    sealed class SequencedHandler(Queue<HttpStatusCode> statuses) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(statuses.Dequeue()) {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/artefacts", StringComparison.Ordinal)
                    ? """{"artefacts":[{"artefact_id":"p1","title":"Findings","owner_user_id":"u1","visibility":"org","latest_version":1,"updated_at":"2026-10-30T10:00:00Z","is_owner":true,"url":"http://server.test/pages/p1"}]}"""
                    : """{"primary":null,"artifacts":[],"diagnostics":[]}""", Encoding.UTF8, "application/json"),
            });
    }

    sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler {
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent("""{"primary":null,"artifacts":[],"diagnostics":[]}""", Encoding.UTF8, "application/json"),
            });

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static (ServerPlanArtifactSource Source, List<ScriptedHandler> Handlers) Build(ConfigRoot config, ProfileContext? profiles, Queue<HttpStatusCode> statuses) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerPlanArtifactSource(config, profiles, ProfileOverrides.None, MachineAuth.None, (_, _, _, _) => {
            var handler = new ScriptedHandler(statuses.Dequeue());
            handlers.Add(handler);
            return Task.FromResult((new HttpClient(handler), AuthStatus.Ok));
        });
        return (source, handlers);
    }

    [Test]
    public async Task A_null_profile_reads_signed_out_without_building_a_client() {
        var (source, handlers) = Build(Config.Root, profiles: null, new());

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(SessionPlansReadKind.SignedOut);
        await Assert.That(handlers).IsEmpty();
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_retires_the_client_and_the_next_read_builds_a_new_one() {
        var (source, handlers) = Build(Config.Root, Resolutions.At("http://server.test", Config.Root), new([HttpStatusCode.Unauthorized, HttpStatusCode.OK]));

        var first = await source.ReadAsync(Session, CancellationToken.None);
        var second = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(first.Kind).IsEqualTo(SessionPlansReadKind.SignedOut);
        await Assert.That(second.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(handlers.Count).IsEqualTo(2);
        await Assert.That(handlers[0].Disposed).IsTrue();
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_pages_read_drops_the_cached_pages_and_documents() {
        using var handler = new SequencedHandler(new([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized]));
        var source = new ServerPlanArtifactSource(Config.Root, Resolutions.At("http://server.test", Config.Root), ProfileOverrides.None,
            MachineAuth.None, (_, _, _, _) => Task.FromResult((new HttpClient(handler, disposeHandler: false), AuthStatus.Ok)));

        var documents = await source.ReadAsync(Session, CancellationToken.None);
        var pages = await source.ReadPagesAsync(Session, CancellationToken.None);
        await Assert.That(documents.Kind).IsEqualTo(SessionPlansReadKind.Ready);
        await Assert.That(pages.Pages.Single().Url).IsEqualTo("http://server.test/pages/p1");
        await Assert.That(source.Cached(Session)).IsNotNull();
        await Assert.That(source.CachedPages(Session)).IsNotNull();

        var lapsed = await source.ReadPagesAsync(Session, CancellationToken.None);

        await Assert.That(lapsed.Kind).IsEqualTo(SessionPlansReadKind.SignedOut);
        await Assert.That(source.CachedPages(Session)).IsNull();
        await Assert.That(source.Cached(Session)).IsNull();
        await source.DisposeAsync();
    }
}
