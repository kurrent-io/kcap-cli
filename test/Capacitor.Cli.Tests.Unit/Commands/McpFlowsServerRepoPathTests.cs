using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A start may name the checkout it reviews: the hosted repository moves to that checkout's
/// root, while the requester stays the session.</summary>
public class McpFlowsServerRepoPathTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Checkout { get; init; }

    const string StartV2 = "/api/flows/review/start/v2";

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static JsonObject StartRequest(string? repoPath) => new() {
        ["params"] = new JsonObject {
            ["name"] = "start_review_flow",
            ["arguments"] = new JsonObject {
                ["kind"]          = "code-review",
                ["target_kind"]   = "pr",
                ["target_ref"]    = "123",
                ["target_title"]  = "some PR",
                ["session_title"] = "Review the thing",
                ["context"]       = "some context",
                ["repo_path"]     = repoPath,
            },
        },
    };

    static (bool IsError, string Text) Unwrap(string response) {
        var result = JsonNode.Parse(response)!.AsObject()["result"]!;

        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    static WireMockServer RefusingServer() {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"no_daemon_available","message":"none"}"""));

        return server;
    }

    [Test]
    public async Task A_named_checkout_is_the_hosted_repository_and_the_session_stays_the_requester() {
        Directory.CreateDirectory(Path.Combine(Checkout.Path, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(Checkout.Path, "src", "deep")).FullName;
        using var server = RefusingServer();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, StartRequest(nested), client, server.Url!,
            cwd: "/tmp/session", repoRoot: "/tmp/session", repoInfo: null);

        var body = JsonNode.Parse(server.LogEntries.Single(e => e.RequestMessage.Path == StartV2).RequestMessage.Body!)!.AsObject();
        var root = new DirectoryInfo(Checkout.Path).FullName;
        await Assert.That(body["repo_path"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(body["requesting_repo_root"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(body["requesting_cwd"]!.GetValue<string>()).IsEqualTo("/tmp/session");
    }

    [Test]
    [Arguments("relative/path")]
    [Arguments("/")]
    public async Task A_path_that_names_no_checkout_is_refused_before_anything_is_posted(string repoPath) {
        using var server = RefusingServer();
        using var client = new HttpClient();

        var (isError, text) = Unwrap(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, StartRequest(repoPath), client, server.Url!,
            cwd: "/tmp/session", repoRoot: "/tmp/session", repoInfo: null));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("repo_path");
        await Assert.That(server.LogEntries).IsEmpty();
    }
}
