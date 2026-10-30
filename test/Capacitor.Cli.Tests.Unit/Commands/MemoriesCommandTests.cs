using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
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
        return await MemoriesCommand.SaveAsync(client, server.Url!, new JsonObject { ["slug"] = "utc-clock", ["audience"] = "org" }, Candidate, update);
    }

    const string Conflict = """{"memory":{"memory_id":"m1","slug":"utc-clock","audience":"org"},"near_duplicate":null,"slug_conflict":true}""";

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
    [Arguments(",\"audience\":\"user\"", "exists as a user memory, skipped (--update overwrites only an existing org memory)")]
    [Arguments("", "exists as a memory of unknown audience, skipped (--update overwrites only an existing org memory)")]
    public async Task Update_never_overwrites_a_memory_not_known_to_have_the_requested_audience(string audience, string expected) {
        using var server = ServerAnswering($$"""{"memory":{"memory_id":"m1"{{audience}}},"near_duplicate":null,"slug_conflict":true}""");
        using var client = new HttpClient();

        var outcome = await MemoriesCommand.SaveAsync(client, server.Url!, new JsonObject { ["audience"] = "org" }, Candidate, update: true);

        await Assert.That(outcome).IsEqualTo(expected);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Method == "PUT")).IsFalse();
    }

    [Test]
    public async Task A_conflict_that_names_no_memory_is_a_failure_line() {
        using var server = ServerAnswering("""{"memory":{"audience":"org"},"slug_conflict":true}""");

        await Assert.That(await Save(server, update: true)).IsEqualTo("failed (the server's conflict answer named no memory)");
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
    [Arguments("""{"memory_id":"m7","audience":"org","repo_hash":"r1"}""", "m7")]
    [Arguments("""{"memory_id":"m7","audience":"user","repo_hash":"r1"}""", null)]
    [Arguments("""{"memory_id":"m7","audience":"org","repo_hash":"r2"}""", null)]
    public async Task An_org_memory_already_at_this_place_is_found_before_saving(string row, string? expected) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/memories/utc-clock").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(row));
        using var client = new HttpClient();

        await Assert.That(await MemoriesCommand.ExistingOrgMemoryAsync(client, server.Url!, "utc-clock", "r1", null)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_slug_with_no_visible_memory_is_not_an_existing_one() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        await Assert.That(await MemoriesCommand.ExistingOrgMemoryAsync(client, server.Url!, "utc-clock", "r1", null)).IsNull();
    }

    [Test]
    public async Task A_claude_project_folder_is_homed_only_at_the_checkout_it_was_recorded_in() {
        var claude  = new ClaudePaths(new UserHome(Tmp.PathTo("home")), null);
        var workdir = Tmp.PathTo("work", "acme-app");

        await Assert.That(MemoriesCommand.PlaceDir(Path.Combine(claude.ProjectDir(workdir), "memory"), workdir, claude)).IsEqualTo(workdir);
        await Assert.That(MemoriesCommand.PlaceDir(Path.Combine(claude.ProjectDir(Tmp.PathTo("work", "other")), "memory"), workdir, claude)).IsNull();
        await Assert.That(MemoriesCommand.PlaceDir(Tmp.PathTo("notes", "memory"), workdir, claude)).IsEqualTo(workdir);
    }

    [Test]
    [Arguments("Hello World_again", "hello-world-again")]
    [Arguments("  --a__b--  ", "a-b")]
    [Arguments("ümlaut ok", "mlaut-ok")]
    [Arguments("!!!", null)]
    public async Task Slugs_follow_the_server_rules(string input, string? expected) =>
        await Assert.That(MemorySlug.Normalize(input)).IsEqualTo(expected);

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Without_a_terminal_the_run_stops_at_once_without_reading_an_answer(bool inputRedirected, bool outputRedirected) {
        // A "y" waiting on stdin would be taken as consent if anything read it.
        var input  = new StringReader("y\n");
        var output = new StringWriter();
        var error  = new StringWriter();

        var stop = await MemoriesCommand.ConfirmAsync(yes: false, inputRedirected, outputRedirected, input, output, error);

        await Assert.That(stop).IsEqualTo(1);
        await Assert.That(input.Peek()).IsEqualTo((int)'y');
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(error.ToString()).Contains("--yes");
    }

    [Test]
    public async Task Yes_goes_ahead_without_asking_even_without_a_terminal() {
        var input  = new StringReader("n\n");
        var output = new StringWriter();

        var stop = await MemoriesCommand.ConfirmAsync(yes: true, inputRedirected: true, outputRedirected: true, input, output, new StringWriter());

        await Assert.That(stop).IsNull();
        await Assert.That(input.Peek()).IsEqualTo((int)'n');
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    [Arguments("y\n", null)]
    [Arguments("n\n", 0)]
    [Arguments("", 0)]
    public async Task At_a_terminal_the_answer_decides(string answer, int? expected) {
        var output = new StringWriter();

        var stop = await MemoriesCommand.ConfirmAsync(yes: false, inputRedirected: false, outputRedirected: false,
                                                      new StringReader(answer), output, new StringWriter());

        await Assert.That(stop).IsEqualTo(expected);
        await Assert.That(output.ToString()).StartsWith("Import? [y/N] ");
    }

    [Test]
    public async Task A_slug_past_64_chars_is_refused() =>
        await Assert.That(MemorySlug.Normalize(new string('a', MemorySlug.MaxLength + 1))).IsNull();
}
