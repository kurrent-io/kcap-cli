using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Discovery-phase tests for <see cref="CopilotImportSource"/> against fake
/// <c>session-state/</c> trees: scaffolding-dir skipping, current-root
/// precedence over the legacy root, workspace.yaml metadata extraction, and
/// the --session / --cwd / --since filters.
/// </summary>
public class CopilotImportSourceTests {
    const string Sid1 = "1053bee4-574f-40e3-84ca-463bd7a82dc2";
    const string Sid2 = "4ae28a73-dd66-46ac-81d2-be94b5e87079";

    [Test]
    public async Task discovery_skips_scaffolding_dirs_without_events_jsonl() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        // Failed-startup scaffolding: workspace.yaml but no events.jsonl.
        Directory.CreateDirectory(Path.Combine(tmp.Path, Sid2));
        await File.WriteAllTextAsync(Path.Combine(tmp.Path, Sid2, "workspace.yaml"), $"id: {Sid2}\ncwd: /work/b\n");

        var source   = new CopilotImportSource(tmp.Path, legacyDirOverride: Path.Combine(tmp.Path, "none"));
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(sessions[0].Vendor).IsEqualTo("copilot");
    }

    [Test]
    public async Task discovery_reads_workspace_yaml_metadata() {
        using var tmp = new TempDir();
        WriteSession(
            tmp.Path, Sid1,
            cwd: "/work/a",
            name: "Create a file hello.txt containing 'hello world'",
            createdAt: "2026-06-10T20:23:25.556Z");

        var source   = new CopilotImportSource(tmp.Path, legacyDirOverride: Path.Combine(tmp.Path, "none"));
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z"));
        await Assert.That(sessions[0].SourceMeta["Name"]).IsEqualTo("Create a file hello.txt containing 'hello world'");
    }

    [Test]
    public async Task discovery_prefers_current_root_over_legacy_for_same_session() {
        using var tmp = new TempDir();
        var current = Path.Combine(tmp.Path, "session-state");
        var legacy  = Path.Combine(tmp.Path, "history-session-state");

        WriteSession(current, Sid1, cwd: "/work/current");
        WriteSession(legacy, Sid1, cwd: "/work/legacy");
        WriteSession(legacy, Sid2, cwd: "/work/legacy-only");

        var source   = new CopilotImportSource(current, legacy);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(2);

        var migrated = sessions.Single(s => s.SessionId == Sid1.Replace("-", ""));
        await Assert.That(migrated.Cwd).IsEqualTo("/work/current");
        await Assert.That(sessions.Any(s => s.SessionId == Sid2.Replace("-", ""))).IsTrue();
    }

    [Test]
    public async Task session_filter_matches_dashless_and_dashed_input() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        WriteSession(tmp.Path, Sid2, cwd: "/work/b");

        var source = new CopilotImportSource(tmp.Path, legacyDirOverride: Path.Combine(tmp.Path, "none"));

        var byDashed = await source.DiscoverAsync(new DiscoveryFilters(null, Sid1, null, 0), CancellationToken.None);
        await Assert.That(byDashed.Count).IsEqualTo(1);
        await Assert.That(byDashed[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));

        var byDashless = await source.DiscoverAsync(
            new DiscoveryFilters(null, Sid1.Replace("-", ""), null, 0), CancellationToken.None);
        await Assert.That(byDashless.Count).IsEqualTo(1);
    }

    [Test]
    public async Task cwd_filter_excludes_other_workspaces_and_sessions_without_cwd() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        WriteSession(tmp.Path, Sid2, cwd: null);   // no workspace.yaml cwd

        var source  = new CopilotImportSource(tmp.Path, legacyDirOverride: Path.Combine(tmp.Path, "none"));
        var matched = await source.DiscoverAsync(new DiscoveryFilters("/work/a", null, null, 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
    }

    [Test]
    public async Task since_filter_gates_on_session_start() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a", createdAt: "2026-06-01T10:00:00Z");
        WriteSession(tmp.Path, Sid2, cwd: "/work/b", createdAt: "2026-06-09T10:00:00Z");

        var source  = new CopilotImportSource(tmp.Path, legacyDirOverride: Path.Combine(tmp.Path, "none"));
        var matched = await source.DiscoverAsync(
            new DiscoveryFilters(null, null, new DateOnly(2026, 6, 5), 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    [Test]
    public async Task workspace_yaml_parser_tolerates_missing_file_and_colon_values() {
        using var tmp = new TempDir();

        await Assert.That(CopilotWorkspaceYaml.TryRead(Path.Combine(tmp.Path, "absent.yaml"))).IsNull();

        var path = Path.Combine(tmp.Path, "workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: 4ae28a73-dd66-46ac-81d2-be94b5e87079
            cwd: /private/tmp/work
            name: Fix the bug: timestamps are wrong
            user_named: false
            created_at: 2026-06-10T20:23:25.556Z
            updated_at: 2026-06-10T20:23:37.838Z
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Cwd).IsEqualTo("/private/tmp/work");
        // Values containing ": " must not be truncated at the second colon.
        await Assert.That(meta.Name).IsEqualTo("Fix the bug: timestamps are wrong");
        await Assert.That(meta.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z"));
        await Assert.That(meta.UpdatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:37.838Z"));
    }

    static void WriteSession(string root, string dashedSid, string? cwd, string? name = null, string? createdAt = null) {
        var dir = Path.Combine(root, dashedSid);
        Directory.CreateDirectory(dir);

        File.WriteAllText(
            Path.Combine(dir, "events.jsonl"),
            """{"type":"session.start","data":{"sessionId":"x"},"id":"11111111-1111-4111-8111-111111111111","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}"""
          + "\n");

        var yaml = $"id: {dashedSid}\n";
        if (cwd is not null) yaml       += $"cwd: {cwd}\n";
        if (name is not null) yaml      += $"name: {name}\n";
        if (createdAt is not null) yaml += $"created_at: {createdAt}\n";
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), yaml);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-copilot-import-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
