using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Harness.Grok;

namespace Capacitor.Cli.Tests.Unit.Harness.Grok;

public class GrokImportSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string ParentId = "01a1359d-bed2-7ce1-bad9-a99e91d9994e";
    const string ChildId  = "01a135a0-64af-7661-94e6-282ec3ef065c";

    GrokImportSource Source() => new(new GrokPaths(new UserHome(Tmp.Path), null), _ => Task.FromResult<RepositoryPayload?>(null), TimeProvider.System);

    static DiscoveryFilters NoFilters => new(FilterCwd: null, FilterSession: null, Since: null, MinLines: 1);

    void Session(string id, string cwd, string? sessionKind = null) {
        var dir = Path.Combine(".grok", "sessions", Uri.EscapeDataString(cwd), id);
        Tmp.CreateFile(Path.Combine(dir, "updates.jsonl"), """{"timestamp":1,"method":"session/update","params":{"sessionId":"x","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"hi"}},"_meta":{"eventId":"x-1","agentTimestampMs":1791900941391}}}""" + "\n");
        var kind = sessionKind is null ? "" : $$""","session_kind":"{{sessionKind}}" """;
        Tmp.CreateFile(Path.Combine(dir, "summary.json"), $$"""{"info":{"id":"{{id}}","cwd":"{{cwd}}"},"created_at":"2026-10-13T14:15:41Z","updated_at":"2026-10-13T14:36:24Z"{{kind}}}""");
    }

    [Test]
    public async Task Discovers_top_level_sessions_with_their_cwd_and_start() {
        Session(ParentId, "/repo/demo");

        var found = (await Source().DiscoverAsync(NoFilters, CancellationToken.None)).Single();

        await Assert.That(found.SessionId).IsEqualTo(ParentId.Replace("-", ""));
        await Assert.That(found.Vendor).IsEqualTo(HarnessId.Grok);
        await Assert.That(found.Cwd).IsEqualTo("/repo/demo");
        await Assert.That(found.FirstTimestamp).IsEqualTo(new DateTimeOffset(2026, 10, 13, 14, 15, 41, TimeSpan.Zero));
    }

    /// <summary>A subagent is imported through its parent, so it is not a session of its own.</summary>
    [Test]
    public async Task Skips_subagent_sessions() {
        Session(ParentId, "/repo/demo");
        Session(ChildId, "/repo/demo", sessionKind: "subagent");

        var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);

        await Assert.That(found.Select(f => f.SessionId)).IsEquivalentTo(new[] { ParentId.Replace("-", "") });
    }

    [Test]
    public async Task A_cwd_filter_narrows_discovery() {
        Session(ParentId, "/repo/demo");
        Session("01a1359d-bed2-7ce1-bad9-a99e91d99999", "/other");

        var found = await Source().DiscoverAsync(NoFilters with { FilterCwd = "/other" }, CancellationToken.None);

        await Assert.That(found.Single().Cwd).IsEqualTo("/other");
    }

    /// <summary>A long cwd's directory is a slug with the cwd in its <c>.cwd</c> file; a cwd may end in
    /// a space, so only the line ending is stripped.</summary>
    [Test]
    public async Task A_slug_directory_s_cwd_keeps_its_trailing_space() {
        var dir = Path.Combine(".grok", "sessions", "long-slug", ParentId);
        Tmp.CreateFile(Path.Combine(dir, "updates.jsonl"), "{}\n");
        Tmp.CreateFile(Path.Combine(".grok", "sessions", "long-slug", ".cwd"), "/repo/demo \n");

        var found = (await Source().DiscoverAsync(NoFilters, CancellationToken.None)).Single();

        await Assert.That(found.Cwd).IsEqualTo("/repo/demo ");
    }

    [Test]
    public async Task Is_available_only_when_the_sessions_dir_exists() {
        await Assert.That(Source().IsAvailable).IsFalse();
        Session(ParentId, "/repo/demo");
        await Assert.That(Source().IsAvailable).IsTrue();
    }
}
