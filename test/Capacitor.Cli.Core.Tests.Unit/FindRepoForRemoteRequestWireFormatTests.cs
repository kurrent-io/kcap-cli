using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The daemon reads <c>resolve_worktrees</c> off the server's snake_case SignalR payload, and a
/// server predating the field — which sends no such property — leaves worktrees unresolved.
/// </summary>
public class FindRepoForRemoteRequestWireFormatTests {
    [Test]
    public async Task ResolveWorktrees_binds_from_the_server_payload() {
        const string json = """{"owner":"o","repo":"r","candidate_paths":["/a"],"resolve_worktrees":true}""";

        var request = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.FindRepoForRemoteRequest);

        await Assert.That(request.ResolveWorktrees).IsTrue();
        await Assert.That(request.CandidatePaths).IsEquivalentTo(["/a"]);
    }

    [Test]
    public async Task A_server_that_omits_the_field_leaves_worktrees_unresolved() {
        const string json = """{"owner":"o","repo":"r","candidate_paths":[]}""";

        var request = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.FindRepoForRemoteRequest);

        await Assert.That(request.ResolveWorktrees).IsFalse();
    }
}
