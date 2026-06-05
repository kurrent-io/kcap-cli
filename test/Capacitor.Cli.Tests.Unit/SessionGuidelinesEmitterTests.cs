using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class SessionGuidelinesEmitterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task BuildFragment_returns_lessons_text_when_server_returns_top_clusters() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(
                        """
                        {
                          "top_clusters": [
                            { "category": "safety",          "text": "always close the writer" },
                            { "category": "maintainability", "text": "prefer JsonNode.Parse for AOT-safe string assignment" }
                          ]
                        }
                        """
                    )
            );

        using var client   = new HttpClient();
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostAsync($"{_server.Url}/hooks/session-start", content);
        var       body     = await response.Content.ReadAsStringAsync();

        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);

        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("Recurring lessons");
        await Assert.That(fragment).Contains("- always close the writer");
        await Assert.That(fragment).Contains("- prefer JsonNode.Parse for AOT-safe string assignment");
        await Assert.That(fragment.TrimStart().StartsWith("{")).IsFalse(); // not a JSON envelope
        await Assert.That(fragment).DoesNotContain("hookSpecificOutput");
        await Assert.That(fragment).DoesNotContain("hookEventName");
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_absent() {
        var body     = """{ "slug": "some-resumed-session" }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_disabled_flag_set() {
        var body = """{ "top_clusters": [ { "category": "safety", "text": "x" } ] }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: true);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_empty_array() {
        var body = """{ "top_clusters": [] }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_top_clusters_is_object_not_array() {
        var body = """{ "top_clusters": { "category": "safety", "text": "x" } }""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_returns_null_when_response_is_top_level_array() {
        var body = """[ { "category": "safety", "text": "x" } ]""";
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task BuildFragment_skips_entries_with_blank_text() {
        var body = """
                   {
                     "top_clusters": [
                       { "category": "safety", "text": ""    },
                       { "category": "safety", "text": "   " },
                       { "category": "safety", "text": "real lesson" }
                     ]
                   }
                   """;
        var fragment = SessionGuidelinesEmitter.BuildFragment(JsonNode.Parse(body), disabled: false);
        await Assert.That(fragment).IsNotNull();
        await Assert.That(fragment!).Contains("- real lesson");
        var bullets = fragment.Split('\n').Count(l => l.StartsWith("- "));
        await Assert.That(bullets).IsEqualTo(1);
    }
}
