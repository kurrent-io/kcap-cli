using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A start may name the checkout it reviews: the hosted repository and the cwd a same-machine
/// borrow probes move to that checkout's root, while the session stays the requester.</summary>
public class McpFlowsServerRepoPathTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Checkout { get; init; }

    const string StartV2 = "/api/flows/review/start/v2";

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static JsonObject StartRequest(string? repoPath, string targetRef = "123", string targetKind = "pr") => new() {
        ["params"] = new JsonObject {
            ["name"] = "start_review_flow",
            ["arguments"] = new JsonObject {
                ["kind"]          = "code-review",
                ["target_kind"]   = targetKind,
                ["target_ref"]    = targetRef,
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
    public async Task A_named_checkout_is_hosted_and_borrowed_and_the_session_stays_the_requester() {
        Directory.CreateDirectory(Path.Combine(Checkout.Path, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(Checkout.Path, "src", "deep")).FullName;
        using var server = RefusingServer();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, StartRequest(nested), client, server.Url!,
            cwd: "/tmp/session", repoRoot: "/tmp/session", repoInfo: null, requestingSessionId: "s1");

        var body = JsonNode.Parse(server.LogEntries.Single(e => e.RequestMessage.Path == StartV2).RequestMessage.Body!)!.AsObject();
        var root = new DirectoryInfo(Checkout.Path).FullName;
        await Assert.That(body["repo_path"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(body["requesting_repo_root"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(body["requesting_cwd"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(body["requesting_session_id"]!.GetValue<string>()).IsEqualTo("s1");
    }

    static readonly RepositoryPayload SessionRepo = new() { Owner = "kurrent-io", RepoName = "kcap-server" };

    async Task<JsonObject> PostedStart(string targetRef, string? repoRoot, RepositoryPayload? repoInfo, string targetKind = "pr") {
        using var server = RefusingServer();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, StartRequest(null, targetRef, targetKind), client, server.Url!,
            cwd: "/tmp/session", repoRoot: repoRoot, repoInfo: repoInfo, requestingSessionId: "s1");

        return JsonNode.Parse(server.LogEntries.Single(e => e.RequestMessage.Path == StartV2).RequestMessage.Body!)!.AsObject();
    }

    [Test]
    [Arguments("https://github.com/kurrent-io/kcap-cli/pull/42", "/tmp/session")]
    [Arguments("kurrent-io/kcap-cli#42", "/tmp/session")]
    [Arguments("kurrent-io/kcap-cli#42", null)]
    public async Task A_pr_in_another_repository_is_hosted_by_that_repository_with_nothing_to_borrow(string targetRef, string? repoRoot) {
        var body = await PostedStart(targetRef, repoRoot, repoRoot is null ? null : SessionRepo);

        await AssertHostedWithoutBorrow(body);
    }

    [Test]
    public async Task A_session_with_no_checkout_borrows_nothing_even_for_its_own_repository() {
        var body = await PostedStart("kurrent-io/kcap-cli#42", null, new RepositoryPayload { Owner = "kurrent-io", RepoName = "kcap-cli" });

        await AssertHostedWithoutBorrow(body);
    }

    static async Task AssertHostedWithoutBorrow(JsonObject body) {
        await Assert.That(body["repo_owner"]!.GetValue<string>()).IsEqualTo("kurrent-io");
        await Assert.That(body["repo_name"]!.GetValue<string>()).IsEqualTo("kcap-cli");
        await Assert.That(body["requesting_cwd"]).IsNull();
        await Assert.That(body["requesting_repo_root"]).IsNull();
        await Assert.That(body["repo_path"]).IsNull();
        await Assert.That(body["requesting_session_id"]!.GetValue<string>()).IsEqualTo("s1");
    }

    [Test]
    [Arguments("https://github.com/Kurrent-IO/KCAP-Server/pull/42", true, "pr")]
    [Arguments("123", true, "pr")]
    [Arguments("kurrent-io/kcap-cli#99999999999", true, "pr")]
    [Arguments("kurrent-io/kcap-cli#42", false, "pr")]
    [Arguments("kurrent-io/kcap-cli#42", true, "file")]
    [Arguments("kurrent-io/kcap-cli#42", true, "branch")]
    public async Task The_session_checkout_stays_borrowed_unless_a_pr_is_known_to_be_elsewhere(string targetRef, bool sessionRepoKnown, string targetKind) {
        var body = await PostedStart(targetRef, "/tmp/session", sessionRepoKnown ? SessionRepo : null, targetKind);

        await Assert.That(body["requesting_cwd"]!.GetValue<string>()).IsEqualTo("/tmp/session");
        await Assert.That(body["requesting_repo_root"]!.GetValue<string>()).IsEqualTo("/tmp/session");
    }

    [Test]
    [Arguments("relative/path")]
    [Arguments("/")]
    [Arguments("/no/such/kcap-checkout")]
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
