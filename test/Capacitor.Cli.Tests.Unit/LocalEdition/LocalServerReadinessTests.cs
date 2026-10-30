using System.Net;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerReadinessTests {
    static readonly LocalServerUnit Unit = new("/x/kcap-server", new LocalRoot("/x"), 51234);

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    [Test]
    public async Task Waits_through_a_starting_server_until_it_reports_ready() {
        var calls   = 0;
        var handler = new Handler(_ => new HttpResponseMessage(++calls < 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        await LocalServerReadiness.WaitAsync(new HttpClient(handler), Unit, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);

        await Assert.That(handler.Requests[0]).IsEqualTo(new Uri("http://localhost:51234/healthz/ready"));
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task A_server_that_never_gets_ready_names_its_log() {
        var time    = new FakeTimeProvider();
        var handler = new Handler(_ => {
            time.Advance(TimeSpan.FromMinutes(11));
            throw new HttpRequestException("connection refused");
        });

        await Assert.That(() => LocalServerReadiness.WaitAsync(new HttpClient(handler), Unit, LocalServerReadiness.StartBudget, time, CancellationToken.None))
            .Throws<LocalLifecycleException>().WithMessageContaining(Unit.OutputLog);
    }

    /// <summary>A server whose logger started writes nothing to the output log, so naming only that file points
    /// at an empty one.</summary>
    [Test]
    public async Task A_server_that_never_gets_ready_names_the_rolling_log_that_holds_its_failure() {
        using var tmp = new TempDir();
        var unit    = LocalServerUnit.ForInstallation(new LocalRoot(tmp.PathTo("local")), 51234);
        var rolling = tmp.CreateFile(["local", "logs", "server-20261030.log"], "KurrentDB could not bind its socket\n");
        tmp.CreateFile(["local", "logs", "server.out.log"], "");
        var time    = new FakeTimeProvider();
        var handler = new Handler(_ => {
            time.Advance(TimeSpan.FromMinutes(11));
            throw new HttpRequestException("connection refused");
        });

        var failure = await Assert.That(() => LocalServerReadiness.WaitAsync(new HttpClient(handler), unit, LocalServerReadiness.StartBudget, time, CancellationToken.None))
            .Throws<LocalLifecycleException>();

        await Assert.That(failure!.Message).Contains(rolling);
        await Assert.That(failure.Message).DoesNotContain(unit.OutputLog);
    }
}
