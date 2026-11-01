using System.Globalization;
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class RepoSessionDtoJsonTests {
    [Test]
    public async Task Listing_response_deserializes_snake_case_rows() {
        const string json = """
            {"items":[{"session_id":"d0ac235c09644610a297b7671e40e1a8","slug":null,"title":"Desktop app RAM growth",
              "owner":{"user_id":"user_01","username":"alexey","display_name":"Alexey Zimarev","avatar_url":null},
              "vendor":"claude","status":"ended","access_level":"full","stale":false,
              "started_at":"2026-10-22T13:04:24.445109+00:00","ended_at":"2026-10-22T16:56:41.708815+00:00",
              "last_activity_at":"2026-10-22T16:56:41.708815+00:00","primary_repo_hash":"830e1690b97dd8c0","is_primary":true,
              "branch":"fix/terminal-replay-loh","cwd":"/Users/alexey/dev/kcap-cli","last_prompt":"(2) is done",
              "write_attempt_paths":[],"write_attempt_count":0,"repo":{"hash":"830e1690b97dd8c0","owner":"kurrent-io","name":"kcap-cli"}}],
             "total":212,"limit":20,"offset":0,"since":null,"until":null,"next_cursor":null}
            """;
        var page = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.RepoSessionsResponseDto)!;
        await Assert.That(page.Total).IsEqualTo(212);
        var row = page.Items.Single();
        await Assert.That(row.SessionId).IsEqualTo("d0ac235c09644610a297b7671e40e1a8");
        await Assert.That(row.Owner!.DisplayName).IsEqualTo("Alexey Zimarev");
        await Assert.That(row.Status).IsEqualTo("ended");
        var endedAt = DateTimeOffset.Parse("2026-10-22T16:56:41.708815+00:00", CultureInfo.InvariantCulture);
        await Assert.That(row.EndedAt).IsEqualTo(endedAt);
        await Assert.That(row.LastActivityAt).IsEqualTo(endedAt);
        await Assert.That(row.Repo!.Name).IsEqualTo("kcap-cli");
        await Assert.That(row.Branch).IsEqualTo("fix/terminal-replay-loh");
    }

    [Test]
    public async Task Recording_identity_deserializes_and_reads_null_when_absent() {
        static RepoSessionDto Row(string field) => JsonSerializer.Deserialize($$"""
            {"items":[{"session_id":"s1","status":"ended","access_level":"full","started_at":"2026-10-22T13:04:24Z",
              "last_activity_at":"2026-10-22T16:56:41Z"{{field}}}],"total":1}
            """, RemoteModelsJsonContext.Default.RepoSessionsResponseDto)!.Items.Single();

        var recorded = Row(""","recording":{"machine_id":"m-1","daemon_name":"laptop","account_id":"acct-1","config_dir":"/Users/me/.claude"}""");

        await Assert.That(recorded.Recording).IsEqualTo(new RepoSessionRecordingDto {
            MachineId = "m-1", DaemonName = "laptop", AccountId = "acct-1", ConfigDir = "/Users/me/.claude"
        });
        await Assert.That(Row("").Recording).IsNull();
        await Assert.That(Row(""","recording":null""").Recording).IsNull();
    }

    /// An older server omits the field, which is not the same as withholding it from the viewer.
    [Test]
    public async Task Pull_requests_tell_absent_from_withheld_from_empty() {
        static RepoSessionDto Row(string field) => JsonSerializer.Deserialize($$"""
            {"items":[{"session_id":"s1","status":"ended","access_level":"full","started_at":"2026-10-22T13:04:24Z",
              "last_activity_at":"2026-10-22T16:56:41Z"{{field}}}],"total":1}
            """, RemoteModelsJsonContext.Default.RepoSessionsResponseDto)!.Items.Single();

        await Assert.That(Row("").PullRequests).IsNull();
        await Assert.That(Row(""","pull_requests":null""").PullRequests!.Items).IsNull();
        await Assert.That(Row(""","pull_requests":[]""").PullRequests!.Items!.Count).IsEqualTo(0);

        const string pr = ""","pull_requests":[{"repo_hash":"h","owner":"kurrent-io","repo_name":"kcap-cli","number":7,"""
            + """ "url":"https://github.com/kurrent-io/kcap-cli/pull/7","title":"Fix","state":null,"merged_at":null,"closed_at":null}]""";
        var listed = Row(pr).PullRequests!;
        await Assert.That(listed.Items!.Single().Number).IsEqualTo(7);
        await Assert.That(listed.Items!.Single().State).IsNull();
        await Assert.That(Row(pr)).IsEqualTo(Row(pr));
    }

    [Test]
    public async Task Owners_deserialize_with_is_me_and_ids() {
        const string json = """[{"user_id":"user_01","username":"alexey","display_name":"Alexey","avatar_url":null,"is_me":true,"ids":["user_01","github:2821205"]}]""";
        var owners = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.SessionOwnerOptionDtoArray)!;
        await Assert.That(owners[0].IsMe).IsTrue();
        await Assert.That(owners[0].Ids).IsEquivalentTo(new[] { "user_01", "github:2821205" });
    }

    [Test]
    public async Task Listing_route_carries_owner_only_when_given() {
        await Assert.That(ApiRoutes.SessionListing("ended", "me", 20, 40)).IsEqualTo("api/sessions/listing?state=ended&limit=20&offset=40&owner=me");
        await Assert.That(ApiRoutes.SessionListing("active", null, 100, 0)).IsEqualTo("api/sessions/listing?state=active&limit=100&offset=0");
        await Assert.That(ApiRoutes.SessionOwners).IsEqualTo("api/sessions/owners");
    }
}
