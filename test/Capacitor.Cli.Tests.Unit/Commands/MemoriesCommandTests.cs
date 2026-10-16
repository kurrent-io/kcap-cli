using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class MemoriesCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly MemoryImportCandidate Candidate = new("a.md", "utc-clock", "Servers run UTC", "feedback", "Convert first.", null, []);

    static WireMockServer ServerAnswering(string saveResponse, int status = 200) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/memories").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(saveResponse));
        server.Given(Request.Create().WithPath("/api/memories/m1").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        return server;
    }

    static async Task<string?> Save(WireMockServer server, bool update) {
        using var client = new HttpClient();
        return await MemoriesCommand.SaveAsync(client, server.Url!, new JsonObject { ["slug"] = "utc-clock" }, Candidate, update);
    }

    const string Conflict = """{"memory":{"memory_id":"m1","slug":"utc-clock"},"near_duplicate":null,"slug_conflict":true}""";

    [Test]
    public async Task A_new_save_reports_its_near_duplicate_and_pending_promotion() {
        using var server = ServerAnswering(
            """{"memory":{"memory_id":"m2"},"near_duplicate":{"memory_id":"m9","slug":"clock-tz","similarity":0.931},"slug_conflict":false,"promotion_status":"pending_approval"}""");

        await Assert.That(await Save(server, update: false))
            .IsEqualTo("created, org promotion pending approval, near-duplicate of clock-tz (0.93)");
    }

    [Test]
    public async Task An_existing_slug_is_left_alone_without_update() {
        using var server = ServerAnswering(Conflict);

        await Assert.That(await Save(server, update: false)).IsEqualTo("exists, skipped (pass --update to overwrite it)");
        await Assert.That(server.LogEntries).Count().IsEqualTo(1);
    }

    [Test]
    public async Task An_existing_slug_is_overwritten_with_update() {
        using var server = ServerAnswering(Conflict);

        await Assert.That(await Save(server, update: true)).IsEqualTo("updated");

        var put  = server.LogEntries.Single(e => e.RequestMessage.Method == "PUT");
        var body = JsonNode.Parse(put.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["content"]!.GetValue<string>()).IsEqualTo("Convert first.");
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("feedback");
    }

    [Test]
    public async Task A_refused_credential_stops_the_import() {
        using var server = ServerAnswering("", status: 401);

        await Assert.That(await Save(server, update: false)).IsNull();
    }

    [Test]
    public async Task A_refused_save_is_a_failure_line() {
        using var server = ServerAnswering("""{"detail":"slug is required"}""", status: 400);

        await Assert.That(await Save(server, update: false)).IsEqualTo("""failed (HTTP 400) {"detail":"slug is required"}""");
    }

    [Test]
    public async Task A_claude_project_memory_folder_decodes_to_its_working_directory() {
        var projects = Tmp.PathTo("claude", "projects");

        await Assert.That(MemoriesCommand.DecodedProjectDir(Tmp.PathTo("claude", "projects", "-work-acme", "memory"), projects))
            .IsEqualTo("/work/acme");
        await Assert.That(MemoriesCommand.DecodedProjectDir(Tmp.PathTo("notes", "memory"), projects)).IsNull();
        await Assert.That(MemoriesCommand.DecodedProjectDir(Tmp.PathTo("claude", "projects", "-work-acme"), projects)).IsNull();
    }

    [Test]
    [Arguments("Hello World_again", "hello-world-again")]
    [Arguments("  --a__b--  ", "a-b")]
    [Arguments("ümlaut ok", "mlaut-ok")]
    [Arguments("!!!", null)]
    public async Task Slugs_follow_the_server_rules(string input, string? expected) =>
        await Assert.That(MemorySlug.Normalize(input)).IsEqualTo(expected);

    [Test]
    public async Task A_slug_past_64_chars_is_refused() =>
        await Assert.That(MemorySlug.Normalize(new string('a', MemorySlug.MaxLength + 1))).IsNull();
}
