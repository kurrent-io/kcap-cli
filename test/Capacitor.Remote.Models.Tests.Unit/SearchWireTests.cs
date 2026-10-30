using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class SearchWireTests {
    [Test]
    public async Task Search_response_reads_a_sessions_lane_and_leaves_transcripts_null() {
        const string json = """
        {"sessions":[{"session_id":"s1","slug":"fix-bug","title":"Fix the bug","started_at":"2026-10-01T10:00:00Z",
          "matched_field":"title","snippet":"Fix the \u0002local\u0003 bug","repo_owner":"kurrent-io","repo_name":"kcap-cli",
          "pr_number":7,"owner_user":"alexey"}],"transcripts":null,"took_ms":12}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.SearchResponseDto)!;
        await Assert.That(dto.Sessions!.Length).IsEqualTo(1);
        await Assert.That(dto.Transcripts).IsNull();
        await Assert.That(dto.Sessions[0].Snippet).IsEqualTo("Fix the \u0002local\u0003 bug");
        await Assert.That(dto.Sessions[0].PrNumber).IsEqualTo(7);
        await Assert.That(dto.Sessions[0].EndedAt).IsNull();
        await Assert.That(dto.TookMs).IsEqualTo(12L);
    }

    [Test]
    public async Task Search_response_reads_a_transcript_hit_without_started_at_or_owner_id() {
        const string json = """
        {"sessions":null,"transcripts":[{"session_id":"s1","snippet":"say \u0002hi\u0003","event_index":42,
          "speaker":"user","turn_index":3,"kind":"message"}],"took_ms":5}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.SearchResponseDto)!;
        var hit = dto.Transcripts![0];
        await Assert.That(dto.Sessions).IsNull();
        await Assert.That(hit.EventIndex).IsEqualTo(42L);
        await Assert.That(hit.TurnIndex).IsEqualTo(3);
        await Assert.That(hit.StartedAt).IsNull();
        await Assert.That(hit.OwnerUserId).IsNull();
    }

    [Test]
    public async Task Activity_session_reads_status_repositories_and_last_event() {
        const string json = """
        {"session_id":"s1","status":"ended","started_at":"2026-10-01T10:00:00Z","ended_at":"2026-10-01T11:00:00Z",
         "last_event_at":"2026-10-01T10:59:00Z","repo_branch":"main",
         "repositories":[{"repo_hash":"h1","owner":"kurrent-io","repo_name":"kcap-cli","branch":"main","is_primary":true},
                         {"repo_hash":"h2","is_primary":false}]}
        """;
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.ActivitySessionDto)!;
        await Assert.That(dto.Status).IsEqualTo("ended");
        await Assert.That(dto.LastEventAt).IsEqualTo(new DateTimeOffset(2026, 10, 1, 10, 59, 0, TimeSpan.Zero));
        await Assert.That(dto.Repositories!.Length).IsEqualTo(2);
        await Assert.That(dto.Repositories[0].IsPrimary).IsTrue();
        await Assert.That(dto.Repositories[1].IsPrimary).IsFalse();
        await Assert.That(dto.Repositories[1].Owner).IsNull();
    }

    [Test]
    public async Task Search_route_escapes_the_query() {
        await Assert.That(ApiRoutes.Search("a b&c", "transcripts", 20))
            .IsEqualTo("api/search?q=a%20b%26c&mode=transcripts&limit=20");
        await Assert.That(ApiRoutes.SessionSummary("a/b")).IsEqualTo("api/sessions/a%2Fb/summary");
    }
}
