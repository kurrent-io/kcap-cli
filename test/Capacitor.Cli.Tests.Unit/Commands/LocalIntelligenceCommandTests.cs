using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The command posts to the local server and reports the outcome, as one JSON line under --json, with an
/// exit code a script can branch on</summary>
[NotInParallel]
public class LocalIntelligenceCommandTests {
    const string Applied =
        """{"mode":"local_agent","harness":"claude","vendor":null,"import_outcomes":true,"backlog_session_touches_per_day":200,"resolving":["titles","outcomes"]}""";

    static LocalIntelligenceCommand Command(string? server, Func<string, string?>? env = null) =>
        new(new FixedCapacitorHttpClient(), new Core.LocalEdition.LocalRoot("/no-local-root")) {
            Server   = () => server,
            Variable = env ?? (_ => null),
        };

    static WireMockServer Serve(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/admin/intelligence").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    [Test]
    public async Task Applied_prints_one_json_line_and_exits_zero() {
        using var server = Serve(200, Applied);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "claude", "--import-outcomes", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo(
            """{"ok":true,"mode":"local_agent","harness":"claude","import_outcomes":true,"backlog_session_touches_per_day":200,"resolving":["titles","outcomes"]}""");
        await Assert.That(server.LogEntries.Single().RequestMessage.Body).Contains("\"import_outcomes\":true");
    }

    [Test]
    public async Task A_refusal_prints_the_code_and_exits_two() {
        using var server = Serve(400, """{"error":"key_rejected: 401"}""");
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url, _ => "sk").HandleAsync(["local", "intelligence", "anthropic", "--json"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo("""{"ok":false,"error":"key_rejected: 401"}""");
    }

    [Test]
    public async Task The_key_is_read_from_the_vendors_variable_and_sent_in_the_body() {
        using var server = Serve(200, Applied);
        using var _ = ConsoleOutput.StartCapture();

        await Command(server.Url, name => name == "COHERE_API_KEY" ? "co-key" : null).HandleAsync(["local", "intelligence", "cohere", "--json"]);

        await Assert.That(server.LogEntries.Single().RequestMessage.Body).Contains("\"api_key\":\"co-key\"");
    }

    [Test]
    public async Task A_provider_without_its_key_under_json_is_refused_before_any_request() {
        using var server = Serve(200, Applied);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "openai", "--json"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stdout.GetCapturedOutput()).Contains("OPENAI_API_KEY");
        await Assert.That(server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task No_local_installation_fails() {
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(null).HandleAsync(["local", "intelligence", "none", "--json"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stdout.GetCapturedOutput()).Contains("\"ok\":false");
    }

    [Test]
    public async Task An_unreachable_server_fails() {
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command("http://127.0.0.1:9").HandleAsync(["local", "intelligence", "none", "--json"]);

        await Assert.That(exit).IsEqualTo(1);
    }
}
