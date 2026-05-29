using System.Net;
using System.Text.Json;
using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;
using Kapacitor.Cli.Tests.Unit.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Import;

public class CursorImportSourceTests {
    [Test]
    public async Task vendor_is_cursor() {
        var src = new CursorImportSource(MakePaths(""));
        await Assert.That(src.Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task does_not_support_title_generation() {
        var src = new CursorImportSource(MakePaths(""));
        await Assert.That(src.SupportsTitleGeneration).IsFalse();
    }

    [Test]
    public async Task is_available_when_global_state_db_exists() {
        var dir = Directory.CreateTempSubdirectory("kapacitor-cursor-source-");
        try {
            var dbPath = Path.Combine(dir.FullName, "state.vscdb");
            await File.WriteAllTextAsync(dbPath, "stub");
            var src = new CursorImportSource(MakePaths(dbPath));
            await Assert.That(src.IsAvailable).IsTrue();
        } finally {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task is_unavailable_when_global_state_db_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kapacitor-cursor-source-missing-{Guid.NewGuid():N}.vscdb");
        var src     = new CursorImportSource(MakePaths(missing));
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task normalize_cursor_session_id_strips_dashes() {
        var dashed   = "abc-1234-5678";
        var dashless = "abc12345678";

        await Assert.That(CursorImportSource.NormalizeCursorSessionId(dashed)).IsEqualTo(dashless);
        await Assert.That(CursorImportSource.NormalizeCursorSessionId(dashless)).IsEqualTo(dashless);
        await Assert.That(CursorImportSource.NormalizeCursorSessionId(dashed))
            .IsEqualTo(CursorImportSource.NormalizeCursorSessionId(dashless));
    }

    [Test]
    public async Task import_session_async_throws_helpfully_when_source_meta_is_null() {
        var src = new CursorImportSource(MakePaths(""));
        var classification = new ImportCommand.SessionClassification {
            SessionId  = "abc",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = null,
        };
        var ctx = new ImportContext(new HttpClient(), "http://localhost", ForcePrivate: false);

        // Direct-cast pattern means null SourceMeta throws NullReferenceException
        // rather than silently falling back to empty strings.
        await Assert.ThrowsAsync<NullReferenceException>(
            () => src.ImportSessionAsync(classification, ctx, CancellationToken.None)
        );
    }

    [Test]
    public async Task import_session_async_throws_helpfully_when_required_key_missing() {
        var src = new CursorImportSource(MakePaths(""));
        var classification = new ImportCommand.SessionClassification {
            SessionId  = "abc",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            // ComposerId / WorkspacePath / GlobalDbPath all missing — direct cast
            // on missing key throws KeyNotFoundException, not a silent fallback.
            SourceMeta = new Dictionary<string, object?>(),
        };
        var ctx = new ImportContext(new HttpClient(), "http://localhost", ForcePrivate: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => src.ImportSessionAsync(classification, ctx, CancellationToken.None)
        );
    }

    [Test]
    public async Task discover_returns_empty_when_workspace_storage_missing() {
        var dir = Directory.CreateTempSubdirectory("kapacitor-cursor-discover-");
        try {
            // GlobalStateDb path exists, but the workspaceStorage dir does not.
            var globalDb = Path.Combine(dir.FullName, "state.vscdb");
            await File.WriteAllTextAsync(globalDb, "stub");

            var paths = new CursorPaths(
                UserDir:             dir.FullName,
                WorkspaceStorageDir: Path.Combine(dir.FullName, "workspaceStorage-missing"),
                GlobalStateDb:       globalDb);

            var src     = new CursorImportSource(paths);
            var filters = new DiscoveryFilters(
                FilterCwd:           null,
                FilterSession:       null,
                Since:               null,
                MinLines:            0,
                CursorWorkspace:     null,
                CursorAllWorkspaces: false);

            var result = await src.DiscoverAsync(filters, CancellationToken.None);

            await Assert.That(result.Count).IsEqualTo(0);
        } finally {
            dir.Delete(recursive: true);
        }
    }

    // ── UnifiedMode guard ────────────────────────────────────────────────────

    [Test]
    public async Task is_agent_mode_returns_true_for_agent_case_insensitive() {
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("agent"))).IsTrue();
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("AGENT"))).IsTrue();
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("Agent"))).IsTrue();
    }

    [Test]
    public async Task is_agent_mode_returns_false_for_chat_inline_ask() {
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("chat"))).IsFalse();
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("inline"))).IsFalse();
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader("ask"))).IsFalse();
        await Assert.That(CursorImportSource.IsAgentMode(MakeHeader(""))).IsFalse();
    }

    [Test]
    public async Task classify_skips_composer_when_unified_mode_is_not_agent() {
        var dir = Directory.CreateTempSubdirectory("kapacitor-cursor-classify-mode-");
        try {
            var globalDb = Path.Combine(dir.FullName, "state.vscdb");
            CursorImportSourceTestFixtures.SeedHeaderOnly(globalDb, "comp-chat", unifiedMode: "chat");

            var src     = new CursorImportSource(MakePaths(globalDb));
            var session = MakeDiscoveredSession("comp-chat", globalDb, dir.FullName);
            var ctx     = new ClassifyContext(new HttpClient(), "http://localhost", MinLines: 0, ExcludedRepos: null, ExcludedPaths: null);

            var result = await src.ClassifyAsync([session], ctx, default);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Excluded);
            await Assert.That(result[0].ProbeErrorReason).IsNotNull();
            await Assert.That(result[0].ProbeErrorReason!).Contains("chat");
        } finally {
            dir.Delete(recursive: true);
        }
    }

    // ── In-flight bubbles guard ──────────────────────────────────────────────

    [Test]
    public async Task has_in_flight_bubbles_returns_false_for_null_or_empty() {
        await Assert.That(CursorImportSource.HasInFlightBubbles(null)).IsFalse();
        await Assert.That(CursorImportSource.HasInFlightBubbles("""{"generatingBubbleIds":[]}""")).IsFalse();
        await Assert.That(CursorImportSource.HasInFlightBubbles("""{"status":"idle"}""")).IsFalse();
    }

    [Test]
    public async Task has_in_flight_bubbles_returns_true_when_array_non_empty() {
        await Assert.That(CursorImportSource.HasInFlightBubbles("""{"generatingBubbleIds":["b1"]}""")).IsTrue();
        await Assert.That(CursorImportSource.HasInFlightBubbles("""{"generatingBubbleIds":["b1","b2"]}""")).IsTrue();
    }

    [Test]
    public async Task has_in_flight_bubbles_returns_false_on_parse_error() {
        await Assert.That(CursorImportSource.HasInFlightBubbles("not-json")).IsFalse();
    }

    [Test]
    public async Task classify_skips_composer_when_bubbles_are_in_flight() {
        // Reuse the richer CursorTestFixtures helper: it sets up workspace+global DBs
        // with unifiedMode=agent, then we ask it to seed generatingBubbleIds=["b1"].
        var (_, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-inflight", generatingBubbleIds: ["b1"]);

        try {
            var src     = new CursorImportSource(paths);
            var session = MakeDiscoveredSession("compinflight", paths.GlobalStateDb, paths.UserDir, composerIdRaw: "comp-inflight");
            var ctx     = new ClassifyContext(new HttpClient(), "http://localhost", MinLines: 0, ExcludedRepos: null, ExcludedPaths: null);

            var result = await src.ClassifyAsync([session], ctx, default);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Excluded);
            await Assert.That(result[0].ProbeErrorReason).IsNotNull();
            await Assert.That(result[0].ProbeErrorReason!).Contains("in-flight");
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Profile-level repo / path exclusions (Finding 1) ─────────────────────

    [Test]
    public async Task classify_marks_session_excluded_when_cli_repo_is_in_excluded_repos() {
        // Seed an agent-mode composer so we get past the unifiedMode and
        // in-flight guards and into the exclusion checks.
        var (_, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-excl-repo");

        try {
            var src = new CursorImportSource(paths);

            // DiscoveredSession with CliOwner/CliRepo populated (as Discover does
            // once per workspace). The session's repo key is "excluded/repo";
            // ExcludedRepos contains the same key (case-insensitive match).
            var session = new DiscoveredSession(
                SessionId:      "compexclrepo",
                Vendor:         "cursor",
                Cwd:            paths.UserDir,
                FirstTimestamp: null,
                SourceMeta:     new Dictionary<string, object?> {
                    ["ComposerId"]    = "comp-excl-repo",
                    ["WorkspacePath"] = paths.UserDir,
                    ["GlobalDbPath"]  = paths.GlobalStateDb,
                    ["CliOwner"]      = "Excluded",
                    ["CliRepo"]       = "Repo",
                });

            var ctx = new ClassifyContext(
                new HttpClient(),
                "http://localhost",
                MinLines:      0,
                ExcludedRepos: ["excluded/repo"],
                ExcludedPaths: null);

            var result = await src.ClassifyAsync([session], ctx, default);

            await Assert.That(result.Count).IsEqualTo(1);
            // We mirror TranscriptFileClassification: status stays New/Partial
            // and the exclusion is signalled via ExcludedRepoKey. The orchestrator
            // groups by ExcludedRepoKey to drive the "include excluded repo?" prompt.
            await Assert.That(result[0].ExcludedRepoKey).IsEqualTo("Excluded/Repo");
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task classify_marks_session_excluded_when_cwd_under_excluded_path() {
        var (_, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-excl-path");

        try {
            var src = new CursorImportSource(paths);

            // The fixture's workspace folder == paths.UserDir. Excluding the parent
            // of that path should match (PathExclusion treats descendants as excluded).
            var parent = Path.GetDirectoryName(paths.UserDir)!;

            var session = new DiscoveredSession(
                SessionId:      "compexclpath",
                Vendor:         "cursor",
                Cwd:            paths.UserDir,
                FirstTimestamp: null,
                SourceMeta:     new Dictionary<string, object?> {
                    ["ComposerId"]    = "comp-excl-path",
                    ["WorkspacePath"] = paths.UserDir,
                    ["GlobalDbPath"]  = paths.GlobalStateDb,
                    ["CliOwner"]      = (string?)null,
                    ["CliRepo"]       = (string?)null,
                });

            var ctx = new ClassifyContext(
                new HttpClient(),
                "http://localhost",
                MinLines:      0,
                ExcludedRepos: null,
                ExcludedPaths: [parent]);

            var result = await src.ClassifyAsync([session], ctx, default);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That(result[0].ExcludedPathKey).IsNotNull();
            // ExcludedPathKey is the normalized form of the matching excluded entry.
            await Assert.That(result[0].ExcludedPathKey!).IsEqualTo(PathExclusion.Normalize(parent));
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Discovery filters: --cwd, --since (Finding 2) ────────────────────────

    [Test]
    public async Task discover_filters_by_cwd_to_one_workspace() {
        // Build a fixture with TWO workspaces under the same Cursor user dir.
        // Then run discovery twice: once unfiltered (both should be returned),
        // once with FilterCwd pointing at workspace A (only A should be returned).
        var paths = CursorTestFixturesExtra.TwoWorkspaces(out var folderA, out var folderB);

        try {
            var src = new CursorImportSource(paths);

            // Baseline: --cursor-all-workspaces returns both.
            var allFilters = new DiscoveryFilters(
                FilterCwd:           null,
                FilterSession:       null,
                Since:               null,
                MinLines:            0,
                CursorWorkspace:     null,
                CursorAllWorkspaces: true);

            var all = await src.DiscoverAsync(allFilters, CancellationToken.None);
            await Assert.That(all.Count).IsEqualTo(2);

            // --cwd <folderA> narrows to A only.
            var cwdFilters = new DiscoveryFilters(
                FilterCwd:           folderA,
                FilterSession:       null,
                Since:               null,
                MinLines:            0,
                CursorWorkspace:     null,
                CursorAllWorkspaces: true);

            var only = await src.DiscoverAsync(cwdFilters, CancellationToken.None);
            await Assert.That(only.Count).IsEqualTo(1);
            await Assert.That(only[0].Cwd).IsEqualTo(folderA);
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task discover_filters_by_since_using_header_timestamp() {
        // Two composers in one workspace: composer-old has createdAt before the
        // cutoff, composer-new after. --since cutoffDate should return only "new".
        // Also asserts that DiscoveredSession.FirstTimestamp is populated from
        // the header (so the orchestrator's post-classify mtime fallback never
        // has to touch a Cursor row).
        var oldMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var newMs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var paths = CursorTestFixturesExtra.WorkspaceWithTwoComposersByTimestamp("comp-old", oldMs, "comp-new", newMs);

        try {
            var src     = new CursorImportSource(paths);
            var cutoff  = new DateOnly(2025, 6, 1);
            var filters = new DiscoveryFilters(
                FilterCwd:           null,
                FilterSession:       null,
                Since:               cutoff,
                MinLines:            0,
                CursorWorkspace:     null,
                CursorAllWorkspaces: true);

            var result = await src.DiscoverAsync(filters, CancellationToken.None);

            await Assert.That(result.Count).IsEqualTo(1);
            await Assert.That((string)result[0].SourceMeta["ComposerId"]!).IsEqualTo("comp-new");
            await Assert.That(result[0].FirstTimestamp).IsNotNull();
            await Assert.That(result[0].FirstTimestamp!.Value.ToUnixTimeMilliseconds()).IsEqualTo(newMs);
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Wire payload assertion (Nit #4) ──────────────────────────────────────

    [Test]
    public async Task import_session_async_includes_cli_owner_and_cli_repo_on_payload() {
        var (workspaceFolder, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-wire");

        try {
            string? capturedBody = null;

            var handler = new TestHttpMessageHandler(async (req, ct) => {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath == "/hooks/cursor-import") {
                    capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : null;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });

            using var http = new HttpClient(handler);
            var       src  = new CursorImportSource(paths);

            var classification = new ImportCommand.SessionClassification {
                SessionId  = "compwire",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata { SessionId = "compwire", Cwd = workspaceFolder },
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = "cursor",
                SourceMeta = new Dictionary<string, object?> {
                    ["ComposerId"]    = "comp-wire",
                    ["WorkspacePath"] = workspaceFolder,
                    ["GlobalDbPath"]  = paths.GlobalStateDb,
                    ["CliOwner"]      = "eventstore",
                    ["CliRepo"]       = "kapacitor-server",
                },
            };

            var ctx     = new ImportContext(http, "http://localhost", ForcePrivate: false);
            var outcome = await src.ImportSessionAsync(classification, ctx, default);

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
            await Assert.That(capturedBody).IsNotNull();
            await Assert.That(capturedBody!).Contains("\"cli_owner\":\"eventstore\"");
            await Assert.That(capturedBody!).Contains("\"cli_repo\":\"kapacitor-server\"");
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Wire-level behaviour: bubble ordering, orphan blobs, payload cap, POST failure ──

    [Test]
    public async Task import_orders_bubbles_by_full_conversation_headers_only() {
        var (workspaceFolder, paths) = CursorTestFixtures.WorkspaceWithBubblesInShuffledOrder("comp-B");

        try {
            string? capturedBody = null;

            var handler = new TestHttpMessageHandler(async (req, ct) => {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath == "/hooks/cursor-import") {
                    capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : null;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var http    = new HttpClient(handler);
            var       src     = new CursorImportSource(paths);
            var       outcome = await src.ImportSessionAsync(
                MakeCursorClassification("compB", "comp-B", workspaceFolder, paths.GlobalStateDb),
                new ImportContext(http, "http://localhost", ForcePrivate: false),
                default
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
            await Assert.That(capturedBody).IsNotNull();

            using var doc     = JsonDocument.Parse(capturedBody!);
            var       bubbles = doc.RootElement.GetProperty("bubbles").EnumerateArray().ToList();
            await Assert.That(bubbles.Count).IsEqualTo(3);
            // Expected order: A, B, C (as declared in fullConversationHeadersOnly),
            // not B, C, A (the SQLite insertion order).
            await Assert.That(bubbles[0].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-A");
            await Assert.That(bubbles[1].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-B");
            await Assert.That(bubbles[2].GetProperty("bubbleId").GetString()).IsEqualTo("comp-B:bub-C");
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task import_content_blobs_excludes_orphan_bubble_blobs() {
        // Fixture: one bubble in fullConversationHeadersOnly (references blob-in),
        // one orphan bubble not in headers (references blob-orphan).
        // Only blob-in must appear in the posted contentBlobs dict.
        var (workspaceFolder, paths) = CursorTestFixtures.WorkspaceWithOrphanBubbleBlobs("comp-C");

        try {
            string? capturedBody = null;

            var handler = new TestHttpMessageHandler(async (req, ct) => {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath == "/hooks/cursor-import") {
                    capturedBody = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : null;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var http    = new HttpClient(handler);
            var       src     = new CursorImportSource(paths);
            var       outcome = await src.ImportSessionAsync(
                MakeCursorClassification("compC", "comp-C", workspaceFolder, paths.GlobalStateDb),
                new ImportContext(http, "http://localhost", ForcePrivate: false),
                default
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
            await Assert.That(capturedBody).IsNotNull();

            using var doc   = JsonDocument.Parse(capturedBody!);
            var       blobs = doc.RootElement.GetProperty("contentBlobs");

            // In-headers bubble's blob must be present
            await Assert.That(blobs.TryGetProperty("composer.content.blob-in", out _)).IsTrue();
            // Orphan bubble's blob must NOT be present
            await Assert.That(blobs.TryGetProperty("composer.content.blob-orphan", out _)).IsFalse();
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task import_returns_failed_when_payload_exceeds_hard_cap() {
        // Use a 1-byte hard cap so the minimal payload always exceeds it,
        // avoiding the need to synthesise a real 10 MB fixture.
        var (workspaceFolder, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-A");

        try {
            var postReached = false;

            var handler = new TestHttpMessageHandler((req, _) => {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath == "/hooks/cursor-import") {
                    postReached = true;
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

            using var http    = new HttpClient(handler);
            var       src     = new CursorImportSource(paths, payloadHardCapBytes: 1);
            var       outcome = await src.ImportSessionAsync(
                MakeCursorClassification("compA", "comp-A", workspaceFolder, paths.GlobalStateDb),
                new ImportContext(http, "http://localhost", ForcePrivate: false),
                default
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
            // POST should never be reached — the cap check comes first.
            await Assert.That(postReached).IsFalse();
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task import_returns_failed_when_post_fails() {
        var (workspaceFolder, paths) = CursorTestFixtures.WorkspaceWithOneComposer("comp-A");

        try {
            var handler = new TestHttpMessageHandler((req, _) => {
                if (req.Method == HttpMethod.Post
                    && req.RequestUri!.AbsolutePath == "/hooks/cursor-import") {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });

            using var http    = new HttpClient(handler);
            var       src     = new CursorImportSource(paths);
            var       outcome = await src.ImportSessionAsync(
                MakeCursorClassification("compA", "comp-A", workspaceFolder, paths.GlobalStateDb),
                new ImportContext(http, "http://localhost", ForcePrivate: false),
                default
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
        } finally {
            try { Directory.Delete(paths.UserDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    static ImportCommand.SessionClassification MakeCursorClassification(
            string sessionId,
            string composerId,
            string workspaceFolder,
            string globalDbPath
        ) => new() {
            SessionId  = sessionId,
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata { SessionId = sessionId, Cwd = workspaceFolder },
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> {
                ["ComposerId"]    = composerId,
                ["WorkspacePath"] = workspaceFolder,
                ["GlobalDbPath"]  = globalDbPath,
                ["CliOwner"]      = (string?)null,
                ["CliRepo"]       = (string?)null,
            },
        };

    static CursorPaths MakePaths(string globalStateDb) =>
        new(
            UserDir:             "/tmp/none",
            WorkspaceStorageDir: "/tmp/none/workspaceStorage",
            GlobalStateDb:       globalStateDb);

    static RawComposerHeader MakeHeader(string unifiedMode) =>
        new(
            ComposerId:        "c",
            UnifiedMode:       unifiedMode,
            Name:              null,
            CreatedAtMs:       0,
            LastUpdatedAtMs:   0,
            Subtitle:          null,
            TotalLinesAdded:   0,
            TotalLinesRemoved: 0,
            FilesChangedCount: 0,
            TrackedGitRepos:   null);

    static DiscoveredSession MakeDiscoveredSession(
            string  sessionId,
            string  globalDb,
            string  workspacePath,
            string? composerIdRaw = null
        ) => new(
            SessionId:      sessionId,
            Vendor:         "cursor",
            Cwd:            workspacePath,
            FirstTimestamp: null,
            SourceMeta:     new Dictionary<string, object?> {
                ["ComposerId"]    = composerIdRaw ?? sessionId,
                ["WorkspacePath"] = workspacePath,
                ["GlobalDbPath"]  = globalDb,
                ["CliOwner"]      = (string?)null,
                ["CliRepo"]       = (string?)null,
            });
}

/// <summary>
/// Minimal HttpMessageHandler that delegates to a user-provided callback.
/// Used to capture POST bodies without spinning up WireMock for a single assertion.
/// </summary>
sealed class TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        handle(req, ct);
}

/// <summary>
/// Tiny SQLite fixtures specific to <see cref="CursorImportSourceTests"/>. The shared
/// <c>CursorTestFixtures</c> always seeds <c>unifiedMode=agent</c>, so we need
/// our own seeder for the non-agent guard test.
/// </summary>
static class CursorImportSourceTestFixtures {
    public static void SeedHeaderOnly(string globalDbPath, string composerId, string unifiedMode) {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={globalDbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = """
                              CREATE TABLE ItemTable    (key TEXT PRIMARY KEY, value TEXT);
                              CREATE TABLE cursorDiskKV (key TEXT PRIMARY KEY, value TEXT);
                              """;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand()) {
            cmd.CommandText = "INSERT INTO ItemTable VALUES ('composer.composerHeaders', @v)";
            cmd.Parameters.AddWithValue(
                "@v",
                $$"""{"allComposers":[{"composerId":"{{composerId}}","unifiedMode":"{{unifiedMode}}","name":"X","createdAt":1,"lastUpdatedAt":1}]}"""
            );
            cmd.ExecuteNonQuery();
        }
    }
}
