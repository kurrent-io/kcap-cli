using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// <summary>
/// Pins the report body's literal camelCase names: the endpoint reads <c>cliVersion</c> and
/// <c>joinId</c>, while the same join value is <c>join_id</c> as a PostHog property, so a body routed
/// through the snake_case JSON context would go unread without any error.
/// </summary>
public class CliSetupReportTests {
    const string Key = "0123456789abcdef0123456789abcdef";

    [Test]
    public async Task Carries_both_literal_camelCase_names() {
        var body = CliSetupReport.Body("1.2.3", Key);

        await Assert.That(body).IsEqualTo($$"""{"cliVersion":"1.2.3","joinId":"{{Key}}"}""");
    }

    [Test]
    public async Task Omits_joinId_when_there_is_no_key() {
        var body = CliSetupReport.Body("1.2.3", null);

        await Assert.That(body).IsEqualTo("""{"cliVersion":"1.2.3"}""");
    }

    [Test]
    public async Task Serialises_an_unknown_version_as_null() {
        var body = CliSetupReport.Body(null, null);

        await Assert.That(body).IsEqualTo("""{"cliVersion":null}""");
    }

    [Test]
    public async Task Never_emits_snake_case() {
        var body = CliSetupReport.Body("1.2.3", Key);

        await Assert.That(body).DoesNotContain("cli_version");
        await Assert.That(body).DoesNotContain("join_id");
    }

    [Test]
    public async Task Produces_parseable_json_for_both_shapes() {
        foreach (var body in new[] { CliSetupReport.Body("1.2.3", Key), CliSetupReport.Body(null, null) }) {
            var parsed = System.Text.Json.Nodes.JsonNode.Parse(body);
            await Assert.That(parsed).IsNotNull();
        }
    }

    [Test]
    public async Task Url_joins_a_server_with_or_without_a_trailing_slash() {
        await Assert.That(CliSetupReport.Url("https://x.example/")).IsEqualTo("https://x.example/api/users/me/cli-setup");
        await Assert.That(CliSetupReport.Url("https://x.example")).IsEqualTo("https://x.example/api/users/me/cli-setup");
    }
}
