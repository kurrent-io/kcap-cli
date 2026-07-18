using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravityImportSource"/> discovery: roots are
/// conversations that are never invoked as a child (per the parent transcript's
/// INVOKE_SUBAGENT steps — see <c>AntigravitySubagents.BuildParentMap</c>), children are
/// attached for subagent import, and IsImportRelevantLine matches the normalizer's
/// event-producing step types. Conversation ids here are GUID-shaped because
/// ChildConversationIdsFromLine only recognizes GUID-shaped ids (matching real brain-dir names).
/// </summary>
public class AntigravityImportSourceTests {
    const string Root  = "aaaaaaaa-0000-0000-0000-00000000a001";
    const string Child = "bbbbbbbb-0000-0000-0000-00000000b001";
    const string Grand = "cccccccc-0000-0000-0000-00000000c001";
    const string SessA = "aaaaaaaa-0000-0000-0000-00000000a00a";
    const string SessB = "bbbbbbbb-0000-0000-0000-00000000b00b";

    // The brain-dir conversation id is dashed on disk, but the surfaced session id is the
    // dashless canonical form (matching live capture). Children stay dashed because
    // they resolve on-disk brain-dir transcript paths.
    static string Dashless(string id) => Guid.Parse(id).ToString("N");

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

    // Appends an INVOKE_SUBAGENT step to convId's transcript naming childId as the spawned
    // conversation — the spawn-time linkage BuildParentMap now reads instead of
    // messages/*.json.
    static void AppendInvoke(string home, string convId, string childId) {
        var dir = Path.Combine(BrainDir(home, convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, "transcript_full.jsonl"), [
            $$"""{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"{{childId}}\"}"}"""
        ]);
    }

    static string UserLine(string ts) =>
        $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{ts}}","content":"<USER_REQUEST>hi</USER_REQUEST>"}""";

    [Test]
    public async Task Discover_returns_roots_only_and_attaches_children() {
        var home = NewHome();
        try {
            WriteTranscript(home, Root, UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, Child, UserLine("2026-07-02T19:01:00Z"));
            AppendInvoke(home, Root, Child); // Root invokes Child as a subagent

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            await Assert.That(source.IsAvailable).IsTrue();

            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0),
                CancellationToken.None);

            // Only Root is a top-level session; Child is imported under it.
            await Assert.That(discovered.Count).IsEqualTo(1);
            var root = discovered[0];
            await Assert.That(root.SessionId).IsEqualTo(Dashless(Root));
            await Assert.That(root.Vendor).IsEqualTo("antigravity");
            await Assert.That(root.FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-07-02T19:00:00Z"));

            var children = (List<string>)root.SourceMeta!["Children"]!;
            await Assert.That(children).Contains(Child);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_nests_transitive_descendants_under_the_top_level_root() {
        var home = NewHome();
        try {
            // Chain Root ← Child ← Grand. Only Root is a session; Child *and* Grand import under it.
            WriteTranscript(home, Root,  UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, Child, UserLine("2026-07-02T19:01:00Z"));
            WriteTranscript(home, Grand, UserLine("2026-07-02T19:02:00Z"));
            AppendInvoke(home, Root,  Child);
            AppendInvoke(home, Child, Grand);

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: null, Since: null, MinLines: 0),
                CancellationToken.None);

            await Assert.That(discovered.Count).IsEqualTo(1);
            await Assert.That(discovered[0].SessionId).IsEqualTo(Dashless(Root));
            var children = (List<string>)discovered[0].SourceMeta!["Children"]!;
            await Assert.That(children.OrderBy(x => x).ToList()).IsEquivalentTo(new List<string> { Child, Grand }.OrderBy(x => x).ToList());
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_honors_the_session_filter() {
        var home = NewHome();
        try {
            WriteTranscript(home, SessA, UserLine("2026-07-02T19:00:00Z"));
            WriteTranscript(home, SessB, UserLine("2026-07-02T19:00:00Z"));

            var source = new AntigravityImportSource(home: home, geminiCliHome: "");
            var discovered = await source.DiscoverAsync(
                new DiscoveryFilters(FilterCwd: null, FilterSession: SessB, Since: null, MinLines: 0),
                CancellationToken.None);

            await Assert.That(discovered.Count).IsEqualTo(1);
            await Assert.That(discovered[0].SessionId).IsEqualTo(Dashless(SessB));
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task Discover_surfaces_a_dashless_session_id_for_a_dashed_conversation_id() {
        // Real Antigravity conversation ids are dashed UUIDs (the brain-dir name). The
        // discovered session id must be the DASHLESS canonical form — matching live capture
        // (the Antigravity hook + `kcap watch` strip dashes) so a session captured live and
        // later re-imported dedupes to one stream, and so `--session` filtering is
        // format-insensitive.
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
