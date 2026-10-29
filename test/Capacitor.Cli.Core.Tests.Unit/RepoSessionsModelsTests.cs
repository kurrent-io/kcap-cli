using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins the wire names the server emits for the repo listing; a renamed property here
/// would deserialize to null silently.</summary>
public class RepoSessionsModelsTests {
    const string Body = """
        {"items":[{"session_id":"abc","slug":"abc-slug","title":"Fix it","owner":{"user_id":"github:1","username":"alice","display_name":"Alice","avatar_url":null},
        "vendor":"claude","status":"active","access_level":"full","stale":true,"started_at":"2026-09-02T09:00:00+00:00","ended_at":null,
        "last_activity_at":"2026-09-02T10:00:00+00:00","primary_repo_hash":"da9c523c68aee2f1","is_primary":false,"branch":"main","cwd":"/work",
        "last_prompt":"do the thing","write_attempt_paths":["/work/a.cs"],"write_attempt_count":1},
        {"session_id":"def","slug":null,"title":null,"owner":null,"vendor":null,"status":"ended","access_level":"overview","stale":false,
        "started_at":"2026-09-01T09:00:00+00:00","ended_at":"2026-09-01T10:00:00+00:00","last_activity_at":"2026-09-01T10:00:00+00:00",
        "primary_repo_hash":null,"is_primary":true,"branch":null,"cwd":null,"last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0}],
        "total":2,"limit":20,"offset":0}
        """;

    [Test]
    public async Task Deserializes_every_field_and_tolerates_nulls() {
        var page = JsonSerializer.Deserialize(Body, CapacitorJsonContext.Default.RepoSessionsResponse)!;

        await Assert.That(page.Total).IsEqualTo(2);
        await Assert.That(page.Limit).IsEqualTo(20);
        await Assert.That(page.Offset).IsEqualTo(0);
        await Assert.That(page.Items.Count).IsEqualTo(2);

        var first = page.Items[0];
        await Assert.That(first.SessionId).IsEqualTo("abc");
        await Assert.That(first.Slug).IsEqualTo("abc-slug");
        await Assert.That(first.Title).IsEqualTo("Fix it");
        await Assert.That(first.Owner!.UserId).IsEqualTo("github:1");
        await Assert.That(first.Owner.Username).IsEqualTo("alice");
        await Assert.That(first.Owner.DisplayName).IsEqualTo("Alice");
        await Assert.That(first.Owner.AvatarUrl).IsNull();
        await Assert.That(first.Vendor).IsEqualTo("claude");
        await Assert.That(first.Status).IsEqualTo("active");
        await Assert.That(first.AccessLevel).IsEqualTo("full");
        await Assert.That(first.Stale).IsTrue();
        await Assert.That(first.StartedAt).IsEqualTo(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(first.EndedAt).IsNull();
        await Assert.That(first.LastActivityAt).IsEqualTo(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(first.PrimaryRepoHash).IsEqualTo("da9c523c68aee2f1");
        await Assert.That(first.IsPrimary).IsFalse();
        await Assert.That(first.Branch).IsEqualTo("main");
        await Assert.That(first.Cwd).IsEqualTo("/work");
        await Assert.That(first.LastPrompt).IsEqualTo("do the thing");
        await Assert.That(first.WriteAttemptPaths).IsEquivalentTo(new[] { "/work/a.cs" });
        await Assert.That(first.WriteAttemptCount).IsEqualTo(1);

        var second = page.Items[1];
        await Assert.That(second.SessionId).IsEqualTo("def");
        await Assert.That(second.Slug).IsNull();
        await Assert.That(second.Title).IsNull();
        await Assert.That(second.Owner).IsNull();
        await Assert.That(second.Vendor).IsNull();
        await Assert.That(second.Status).IsEqualTo("ended");
        await Assert.That(second.AccessLevel).IsEqualTo("overview");
        await Assert.That(second.Stale).IsFalse();
        await Assert.That(second.StartedAt).IsEqualTo(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(second.EndedAt).IsEqualTo(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(second.LastActivityAt).IsEqualTo(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(second.PrimaryRepoHash).IsNull();
        await Assert.That(second.IsPrimary).IsTrue();
        await Assert.That(second.Branch).IsNull();
        await Assert.That(second.Cwd).IsNull();
        await Assert.That(second.LastPrompt).IsNull();
        await Assert.That(second.WriteAttemptPaths).IsEmpty();
        await Assert.That(second.WriteAttemptCount).IsEqualTo(0);
    }

    const string Windowed = """
        {"items":[{"session_id":"abc","slug":null,"title":"Fix it","owner":null,"vendor":"claude","status":"ended","access_level":"full","stale":false,
        "started_at":"2026-09-20T09:00:00+00:00","ended_at":"2026-09-20T11:00:00+00:00","last_activity_at":"2026-09-20T11:00:00+00:00",
        "primary_repo_hash":"da9c523c68aee2f1","is_primary":true,"branch":"main","cwd":"/work","last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0,
        "repo":{"hash":"da9c523c68aee2f1","owner":"acme","name":"widgets"}},
        {"session_id":"def","slug":null,"title":null,"owner":null,"vendor":null,"status":"ended","access_level":"full","stale":false,
        "started_at":"2026-09-19T09:00:00+00:00","ended_at":"2026-09-19T10:00:00+00:00","last_activity_at":"2026-09-19T10:00:00+00:00",
        "primary_repo_hash":"0badf00d12345678","is_primary":true,"branch":null,"cwd":null,"last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0,
        "repo":{"hash":"0badf00d12345678","owner":null,"name":null}},
        {"session_id":"ghi","slug":null,"title":null,"owner":null,"vendor":null,"status":"ended","access_level":"full","stale":false,
        "started_at":"2026-09-18T09:00:00+00:00","ended_at":"2026-09-18T10:00:00+00:00","last_activity_at":"2026-09-18T10:00:00+00:00",
        "primary_repo_hash":null,"is_primary":false,"branch":null,"cwd":null,"last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0,
        "repo":null}],
        "total":3,"limit":20,"offset":0,"since":"2026-09-14T00:00:00+00:00","until":null,"next_cursor":"eyJ2IjoxfQ"}
        """;

    [Test]
    public async Task Reads_the_window_echo_the_cursor_and_the_repository() {
        var page = JsonSerializer.Deserialize(Windowed, CapacitorJsonContext.Default.RepoSessionsResponse)!;

        await Assert.That(page.Since).IsEqualTo(new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(page.Until).IsNull();
        await Assert.That(page.NextCursor).IsEqualTo("eyJ2IjoxfQ");

        await Assert.That(page.Items[0].Repo!.Hash).IsEqualTo("da9c523c68aee2f1");
        await Assert.That(page.Items[0].Repo!.Owner).IsEqualTo("acme");
        await Assert.That(page.Items[0].Repo!.Name).IsEqualTo("widgets");
        await Assert.That(page.Items[1].Repo!.Hash).IsEqualTo("0badf00d12345678");
        await Assert.That(page.Items[1].Repo!.Owner).IsNull();
        await Assert.That(page.Items[1].Repo!.Name).IsNull();
        await Assert.That(page.Items[2].Repo).IsNull();
    }

    /// <summary>A server that predates the window sends none of the four fields. They must read as
    /// absent, since that absence is how the command tells such a server from a current one.</summary>
    [Test]
    public async Task A_body_without_the_new_fields_reads_them_as_absent() {
        var page = JsonSerializer.Deserialize(Body, CapacitorJsonContext.Default.RepoSessionsResponse)!;

        await Assert.That(page.Since).IsNull();
        await Assert.That(page.Until).IsNull();
        await Assert.That(page.NextCursor).IsNull();
        await Assert.That(page.Items[0].Repo).IsNull();
    }
}
