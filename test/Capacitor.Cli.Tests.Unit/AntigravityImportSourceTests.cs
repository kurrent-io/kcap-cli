using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravityImportSource"/> discovery (AI-1160): roots are
/// conversations that are not a subagent of another (per the messages linkage), children
/// are attached for subagent import, and IsImportRelevantLine matches the normalizer's
/// event-producing step types.
/// </summary>
public class AntigravityImportSourceTests {
    static string NewHome() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-agimp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    static string BrainDir(string home, string convId) =>
        Path.Combine(home, ".gemini", "antigravity", "brain", convId);

    static void WriteTranscript(string home, string convId, params string[] lines) {
        var dir = Path.Combine(BrainDir(home, convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), lines);
    }

    static void WriteMessage(string home, string owner, string sender, string recipient) {
        var dir = Path.Combine(BrainDir(home, owner), ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sender + ".json"),
            new JsonObject { ["sender"] = sender, ["recipient"] = recipient }.ToJsonString());
    }

    static string UserLine(string ts) =>
        $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{ts}}","content":"<USER_REQUEST>hi</USER_REQUEST>"}""";

    [Test]
    public async Task Discover_returns_roots_only_and_attaches_children() {
        var home = NewHome();
        try {
            WriteTranscript(home, "ROOT", UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, "CHILD", UserLine("2026-07-02T19:01:00Z"));
            WriteMessage(home, owner: "ROOT", sender: "CHILD", recipient: "ROOT"); // CHILD is a subagent of ROOT

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            await Assert.That(source.IsAvailable).IsTrue();

            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0),
                CancellationToken.None);

            // Only ROOT is a top-level session; CHILD is imported under it.
            await Assert.That(discovered.Count).IsEqualTo(1);
            var root = discovered[0];
            await Assert.That(root.SessionId).IsEqualTo("ROOT");
            await Assert.That(root.Vendor).IsEqualTo("antigravity");
            await Assert.That(root.FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:00:00Z"));

            var children = (List<string>)root.SourceMeta!["Children"]!;
            await Assert.That(children).Contains("CHILD");
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_nests_transitive_descendants_under_the_top_level_root() {
        var home = NewHome();
        try {
            // Chain ROOT ← CHILD ← GRAND. Only ROOT is a session; CHILD *and* GRAND import under it.
            WriteTranscript(home, "ROOT",  UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, "CHILD", UserLine("2026-07-02T19:01:00Z"));
            WriteTranscript(home, "GRAND", UserLine("2026-07-02T19:02:00Z"));
            WriteMessage(home, owner: "ROOT",  sender: "CHILD", recipient: "ROOT");
            WriteMessage(home, owner: "CHILD", sender: "GRAND", recipient: "CHILD");

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0),
                CancellationToken.None);

            await Assert.That(discovered.Count).IsEqualTo(1);
            await Assert.That(discovered[0].SessionId).IsEqualTo("ROOT");
            var children = (List<string>)discovered[0].SourceMeta!["Children"]!;
            await Assert.That(children.OrderBy(x => x).ToList()).IsEquivalentTo(new List<string> { "CHILD", "GRAND" });
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_honors_the_session_filter() {
        var home = NewHome();
        try {
            WriteTranscript(home, "A", UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, "B", UserLine("2026-07-02T19:00:00Z"));

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: "B", Since: null, MinLines: 0),
                CancellationToken.None);

            await Assert.That(discovered.Count).IsEqualTo(1);
            await Assert.That(discovered[0].SessionId).IsEqualTo("B");
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_surfaces_a_dashless_session_id_for_a_dashed_conversation_id() {
        // Real Antigravity conversation ids are dashed UUIDs (the brain-dir name). The
        // discovered session id must be the DASHLESS canonical form — matching live capture
        // (the Antigravity hook + `kcap watch` strip dashes) so a session captured live and
        // later re-imported dedupes to one stream, and so `--session` filtering is
        // format-insensitive (AI-1238).
        const string dashed = "11110000-0000-4000-8000-000000000001";
        var dashless = Guid.Parse(dashed).ToString("N");

        var home = NewHome();
        try {
            WriteTranscript(home, dashed, UserLine("2026-07-02T19:00:00Z"));

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0),
                CancellationToken.None);

            await Assert.That(discovered.Count).IsEqualTo(1);
            await Assert.That(discovered[0].SessionId).IsEqualTo(dashless);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_session_filter_accepts_either_dashed_or_dashless_form() {
        const string dashed = "11110000-0000-4000-8000-000000000001";
        var dashless = Guid.Parse(dashed).ToString("N");

        var home = NewHome();
        try {
            WriteTranscript(home, dashed, UserLine("2026-07-02T19:00:00Z"));
            var source = new AntigravityImportSource(home: home, geminiCliHome: "");

            // Dashed input matches.
            var byDashed = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: dashed, Since: null, MinLines: 0),
                CancellationToken.None);
            await Assert.That(byDashed.Count).IsEqualTo(1);
            await Assert.That(byDashed[0].SessionId).IsEqualTo(dashless);

            // Dashless input (the form live capture reports) also matches.
            var byDashless = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: dashless, Since: null, MinLines: 0),
                CancellationToken.None);
            await Assert.That(byDashless.Count).IsEqualTo(1);
            await Assert.That(byDashless[0].SessionId).IsEqualTo(dashless);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task IsImportRelevantLine_matches_event_producing_steps_only() {
        await Assert.That(AntigravityImportSource.IsImportRelevantLine(UserLine("2026-07-02T19:00:00Z"))).IsTrue();
        await Assert.That(AntigravityImportSource.IsImportRelevantLine(
            """{"type":"PLANNER_RESPONSE","content":"ok"}""")).IsTrue();
        await Assert.That(AntigravityImportSource.IsImportRelevantLine(
            """{"type":"RUN_COMMAND","content":"ran"}""")).IsTrue();
        // Noise steps never advance the watermark.
        await Assert.That(AntigravityImportSource.IsImportRelevantLine(
            """{"type":"CHECKPOINT"}""")).IsFalse();
        await Assert.That(AntigravityImportSource.IsImportRelevantLine(
            """{"type":"GENERIC","content":"active subagents"}""")).IsFalse();
        await Assert.That(AntigravityImportSource.IsImportRelevantLine("not json")).IsFalse();
    }

    [Test]
    public async Task Discover_is_empty_when_no_antigravity_data() {
        var home = NewHome();
        try {
            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            await Assert.That(source.IsAvailable).IsFalse();
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
            await Assert.That(discovered.Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }
}
