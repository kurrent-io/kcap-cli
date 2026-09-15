using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class PendingLaunchDtoJsonTests {
    [Test]
    public async Task A_pending_launch_serializes_in_snake_case_after_the_agents() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("main", "1", "u", "connected", 5, 0),
            [],
            [new PendingLaunchDto("p1", "claude", "/repo", "Fix the flaky test", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "spawned")]);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);

        await Assert.That(json).EndsWith(
            """
            "agents":[],"pending":[{"id":"p1","vendor":"claude","repo_path":"/repo","title":"Fix the flaky test","created_at":"2026-09-01T00:00:00Z","stage":"spawned"}]}
            """);
    }

    /// A daemon that predates the field sends no `pending` member; the client reads that as unknown.
    [Test]
    public async Task A_payload_without_pending_reads_as_null() {
        var json =
            """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":0},"agents":[]}""";

        var dto = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

        await Assert.That(dto.Pending).IsNull();
    }
}
