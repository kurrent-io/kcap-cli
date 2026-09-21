using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// What the source itself decides: which outcomes stand in for a missing sign-in, and which read
/// retires the client. No network: the factory is injected and the handler answers in-process.
public class ServerPlanSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";

    sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler {
        public bool Disposed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static (ServerPlanSource Source, List<ScriptedHandler> Handlers) Build(ConfigRoot config, ProfileContext? profiles, Queue<HttpStatusCode> statuses) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerPlanSource(config, profiles, ProfileOverrides.None, MachineAuth.None, (_, _, _, _) => {
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
}
