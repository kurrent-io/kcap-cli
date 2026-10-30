using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// What the source itself decides: a missing sign-in reads signed out without a client, and a
/// 401 retires the client so the next read builds a fresh one. No network.
public class ServerPageSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";
    static readonly Capacitor.Cli.Core.Http.ArtefactDto Page = new("a1", "Poll", "u1", "org", 1, DateTimeOffset.UtcNow, false, "https://srv.test/pages/a1");

    sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler {
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent("""{"artefacts":[]}""", Encoding.UTF8, "application/json"),
            });

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static (ServerPageSource Source, List<ScriptedHandler> Handlers) Build(ConfigRoot config, ProfileContext? profiles, Queue<HttpStatusCode> statuses) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerPageSource(config, profiles, ProfileOverrides.None, MachineAuth.None, (_, _, _, _) => {
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
    public async Task Content_and_bridge_calls_without_a_profile_read_signed_out() {
        var (source, handlers) = Build(Config.Root, profiles: null, new());
        using var payload = System.Text.Json.JsonDocument.Parse("{}");

        await Assert.That(await source.ReadContentAsync("a1", null, CancellationToken.None)).IsTypeOf<PageContentRead.SignedOut>();
        await Assert.That((await source.SubmitAsync("a1", 1, payload.RootElement, CancellationToken.None)).Status).IsEqualTo(401);
        await Assert.That((await source.MineAsync("a1", 1, CancellationToken.None)).Status).IsEqualTo(401);
        await Assert.That((await source.ResultsAsync("a1", 1, CancellationToken.None)).Status).IsEqualTo(401);
        await Assert.That(await source.ResultsAccessAsync("a1", Page, CancellationToken.None)).IsEqualTo(PageResultsAccess.SignedOut);
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
    public async Task The_viewer_name_is_the_stored_sign_in_name_or_empty() {
        await using var named = new ServerPageSource(Config.Root, null, ProfileOverrides.None, MachineAuth.None, viewerName: _ => Task.FromResult<string?>("Ada"));
        await using var unnamed = new ServerPageSource(Config.Root, null, ProfileOverrides.None, MachineAuth.None, viewerName: _ => Task.FromResult<string?>(null));
        await using var failing = new ServerPageSource(Config.Root, null, ProfileOverrides.None, MachineAuth.None, viewerName: _ => throw new IOException("locked"));
        await using var none = new ServerPageSource(Config.Root, null, ProfileOverrides.None, MachineAuth.None);

        await Assert.That(await named.ViewerNameAsync(CancellationToken.None)).IsEqualTo("Ada");
        await Assert.That(await unnamed.ViewerNameAsync(CancellationToken.None)).IsEqualTo("");
        await Assert.That(await failing.ViewerNameAsync(CancellationToken.None)).IsEqualTo("");
        await Assert.That(await none.ViewerNameAsync(CancellationToken.None)).IsEqualTo("");
    }
}
