using System.Net;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel]
public class RecapContinuationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Current  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    RecapContinuation Command() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), TimeProvider.System);

    static HttpClient Serving(Func<string, (int, string)> byPath) => new(new Answers(byPath));

    [Test]
    public async Task Prints_the_continued_block_and_exits_zero() {
        using var output = ConsoleOutput.StartCapture();
        using var client = Serving(path => path.EndsWith("/summary", StringComparison.Ordinal) ? (200, """{"status":"ended"}""") : (200, "[]"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, Current, force: false);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(output.GetCapturedOutput()).Contains($"## Continued from session {Previous}");
    }

    [Test]
    public async Task A_refusal_exits_two_on_stderr() {
        using var error  = ConsoleOutput.StartErrorCapture();
        using var client = Serving(_ => (200, "{}"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, Previous, force: false);

        await Assert.That(code).IsEqualTo(RecapContinuation.Refused);
        await Assert.That(error.GetCapturedError()).Contains("cannot continue itself");
    }

    [Test]
    public async Task No_current_session_is_a_refusal_naming_the_fix() {
        using var error  = ConsoleOutput.StartErrorCapture();
        using var client = Serving(_ => (200, "{}"));

        var code = await Command().RunWithAsync(client, "http://x", Previous, current: null, force: false);

        await Assert.That(code).IsEqualTo(RecapContinuation.Refused);
        await Assert.That(error.GetCapturedError()).Contains("inside the session that takes over");
    }

    [Test]
    public async Task A_401_on_a_write_prints_the_report_then_the_login_notice_and_exits_one() {
        using var console = ConsoleOutput.StartFullCapture();
        using var client  = Serving(path => path switch {
            $"/api/sessions/{Previous}/summary"   => (200, """{"status":"ended"}"""),
            $"/api/work-items/session/{Previous}" => (200, """[{"work_item_id":"w1","label":"W1"}]"""),
            "/api/work-items/declare"             => (401, ""),
            _                                     => (200, "[]"),
        });

        var code = await Command().RunWithAsync(client, "http://x", Previous, Current, force: false);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(console.GetCapturedOutput()).Contains($"## Continued from session {Previous}");
        await Assert.That(console.GetCapturedError()).Contains("kcap login");
    }

    sealed class Answers(Func<string, (int Status, string Body)> byPath) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var (status, body) = byPath(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }
}
