using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A start may carry a patch file and the commit it applies to: both go only to the v5 start route,
/// with nothing offered to borrow, and anything wrong with them is refused before a request is sent.</summary>
public class McpFlowsServerPatchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Files { get; init; }

    const string StartV5 = "/api/flows/review/start/v5";
    const string Base    = "0123456789abcdef0123456789abcdef01234567";
    const string Diff    = "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n";

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    string PatchFile(byte[] content) {
        var path = Files.PathTo($"{Guid.NewGuid():N}.patch");
        File.WriteAllBytes(path, content);

        return path;
    }

    static JsonObject StartRequest(string tool, string? patchPath, string? baseCommit, string? mode = null) {
        var arguments = new JsonObject {
            ["target_kind"]   = "pr",
            ["target_ref"]    = "123",
            ["target_title"]  = "some PR",
            ["session_title"] = "Review the thing",
            ["context"]       = "some context",
            ["patch_path"]    = patchPath,
            ["base_commit"]   = baseCommit,
            ["mode"]          = mode,
        };
        if (tool == "start_review_flow") arguments["kind"] = "code-review";
        else arguments["definition_id"] = "code-review";

        return new JsonObject { ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } };
    }

    static (bool IsError, string Text) Unwrap(string response) {
        var result = JsonNode.Parse(response)!.AsObject()["result"]!;

        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    static WireMockServer Answering(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    async Task<string> Call(WireMockServer server, JsonObject request, string? savedVendor = null) {
        using var client = new HttpClient();

        return await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, request, client, server.Url!,
            cwd: "/tmp/session", repoRoot: "/tmp/session", repoInfo: null, requestingSessionId: "s1",
            reviewerVendorPreference: () => Task.FromResult(new McpFlowsServer.SavedReviewerVendor(savedVendor, "default")));
    }

    [Test]
    [Arguments("start_review_flow")]
    [Arguments("start_flow")]
    public async Task A_patch_start_posts_the_patch_and_base_to_v5_and_offers_nothing_to_borrow(string tool) {
        using var server = Answering(400, """{"error":"no_daemon_available","message":"none"}""");

        await Call(server, StartRequest(tool, PatchFile(System.Text.Encoding.UTF8.GetBytes(Diff)), Base.ToUpperInvariant()));

        var entry = server.LogEntries.Single();
        await Assert.That(entry.RequestMessage.Path).IsEqualTo(StartV5);
        var body = JsonNode.Parse(entry.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["patch"]!.GetValue<string>()).IsEqualTo(Diff);
        await Assert.That(body["base_commit"]!.GetValue<string>()).IsEqualTo(Base);
        await Assert.That(body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(5);
        await Assert.That(body["requesting_cwd"]).IsNull();
        await Assert.That(body["requesting_repo_root"]!.GetValue<string>()).IsEqualTo("/tmp/session");
    }

    /// <summary>No other start route carries the patch, so a server without v5 is reported and nothing else is
    /// posted.</summary>
    [Test]
    public async Task A_server_without_v5_is_reported_and_no_other_route_is_tried() {
        using var server = Answering(404, "");

        var (isError, text) = Unwrap(await Call(server, StartRequest("start_review_flow", PatchFile(System.Text.Encoding.UTF8.GetBytes(Diff)), Base)));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("patch_protocol_required");
        await Assert.That(server.LogEntries.Select(e => e.RequestMessage.Path)).IsEquivalentTo([StartV5]);
    }

    /// <summary>The saved-vendor retry of a patch start reports a missing v5 the same way the first post does.</summary>
    [Test]
    public async Task A_saved_vendor_retry_that_finds_no_v5_is_reported_as_a_patch_refusal() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV5).UsingPost()).InScenario("retry").WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBody("""{"error":"reviewer_vendor_required","message":"no reviewer vendor"}"""));
        server.Given(Request.Create().WithPath(StartV5).UsingPost()).InScenario("retry").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(404));

        var (isError, text) = Unwrap(await Call(server,
            StartRequest("start_review_flow", PatchFile(System.Text.Encoding.UTF8.GetBytes(Diff)), Base), savedVendor: "codex"));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("patch_protocol_required");
        await Assert.That(server.LogEntries.Select(e => e.RequestMessage.Path)).IsEquivalentTo([StartV5, StartV5]);
    }

    /// <summary>Blank patch arguments name no patch, so the start takes its usual route and a 404 there is not
    /// blamed on a missing v5.</summary>
    [Test]
    public async Task Blank_patch_arguments_start_without_a_patch_and_a_404_is_not_a_patch_refusal() {
        using var server = Answering(404, "");

        var (isError, text) = Unwrap(await Call(server, StartRequest("start_review_flow", " ", "")));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).DoesNotContain("patch_protocol_required");
        await Assert.That(server.LogEntries.Select(e => e.RequestMessage.Path)).DoesNotContain(StartV5);
    }

    /// <summary>A retried post sends the bytes read for the call, not the file as it is by then.</summary>
    [Test]
    public async Task A_retried_patch_start_posts_the_patch_read_once() {
        var path = PatchFile(System.Text.Encoding.UTF8.GetBytes(Diff));
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV5).UsingPost()).InScenario("retry").WillSetStateTo("second")
            .RespondWith(Response.Create().WithCallback(_ => {
                File.Delete(path);
                return new WireMock.ResponseMessage {
                    StatusCode = 409,
                    BodyData   = new WireMock.Util.BodyData {
                        BodyAsString     = """{"error":"flow_settlement_busy","message":"busy"}""",
                        DetectedBodyType = WireMock.Types.BodyType.String,
                    },
                };
            }));
        server.Given(Request.Create().WithPath(StartV5).UsingPost()).InScenario("retry").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"no_daemon_available","message":"none"}"""));
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, StartRequest("start_review_flow", path, Base), client, server.Url!,
            cwd: "/tmp/session", repoRoot: "/tmp/session", repoInfo: null, requestingSessionId: "s1",
            backoff: new SettlementBackoff(() => 0.0));

        var posts = server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!["patch"]!.GetValue<string>()).ToList();
        await Assert.That(posts).Count().IsEqualTo(2);
        await Assert.That(posts.All(p => p == Diff)).IsTrue();
    }

    public static IEnumerable<Func<(string Name, Func<McpFlowsServerPatchTests, (string? Path, string? Base)> Arguments)>> Refusals() {
        yield return () => ("path without base", t => (t.PatchFile("+x\n"u8.ToArray()), null));
        yield return () => ("base without path", _ => (null, Base));
        yield return () => ("short sha", t => (t.PatchFile("+x\n"u8.ToArray()), "abc123"));
        yield return () => ("relative path", _ => ("relative.patch", Base));
        yield return () => ("missing file", t => (t.Files.PathTo("absent.patch"), Base));
        yield return () => ("empty file", t => (t.PatchFile([]), Base));
        yield return () => ("too large", t => (t.PatchFile(new byte[McpFlowsServer.MaxPatchBytes + 1]), Base));
        yield return () => ("not UTF-8", t => (t.PatchFile([0xC3, 0x28]), Base));
    }

    [Test]
    public async Task A_patch_with_context_only_is_refused_before_anything_is_posted() {
        using var server = Answering(500, "");

        var (isError, text) = Unwrap(await Call(server,
            StartRequest("start_review_flow", PatchFile("+x\n"u8.ToArray()), Base, mode: "context-only")));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("context-only");
        await Assert.That(server.LogEntries).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(Refusals))]
    public async Task A_bad_patch_is_refused_before_anything_is_posted(
            (string Name, Func<McpFlowsServerPatchTests, (string? Path, string? Base)> Arguments) c) {
        using var server = Answering(500, "");
        var (path, baseCommit) = c.Arguments(this);

        var (isError, _) = Unwrap(await Call(server, StartRequest("start_review_flow", path, baseCommit)));

        await Assert.That(isError).IsTrue();
        await Assert.That(server.LogEntries).IsEmpty();
    }
}
