using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Import;

public class CursorImportSourceTests {
    [Test]
    public async Task vendor_is_cursor() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task does_not_support_title_generation() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.SupportsTitleGeneration).IsFalse();
    }

    [Test]
    public async Task is_available_when_projects_dir_exists() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.IsAvailable).IsTrue();
    }

    [Test]
    public async Task is_unavailable_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-cursor-missing-{Guid.NewGuid():N}");
        var src     = new CursorImportSource(missing, missing);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task normalize_cursor_session_id_strips_dashes() {
        await Assert.That(CursorImportSource.NormalizeCursorSessionId("abc-1234-5678"))
            .IsEqualTo("abc12345678");
    }

    [Test]
    public async Task encode_workspace_path_strips_leading_slash_and_replaces_separators() {
        await Assert.That(CursorImportSource.EncodeWorkspacePath("/Users/me/dev/foo-bar"))
            .IsEqualTo("Users-me-dev-foo-bar");
    }

    [Test]
    public async Task discover_returns_empty_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-cursor-missing-{Guid.NewGuid():N}");
        var src     = new CursorImportSource(missing, missing);
        var result  = await src.DiscoverAsync(Filters(), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discover_walks_jsonl_files() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"x\":1}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":2}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(2);
        await Assert.That(got.Select(s => s.SessionId)).Contains("11111111111111111111111111111111");
        await Assert.That(got.Select(s => s.SessionId)).Contains("22222222222222222222222222222222");
        await Assert.That(got.All(s => s.Vendor == "cursor")).IsTrue();
    }

    [Test]
    public async Task discover_resolves_cwd_via_workspace_storage_when_sanitized_matches() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/foo");
        fx.AddSession("Users-me-dev-foo", "33333333-3333-3333-3333-333333333333", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsEqualTo("/Users/me/dev/foo");
    }

    [Test]
    public async Task discover_leaves_cwd_null_when_sanitized_not_in_workspace_storage() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-someone-else-proj", "44444444-4444-4444-4444-444444444444", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsNull();
    }

    [Test]
    public async Task discover_applies_session_filter_dashless() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "55555555-5555-5555-5555-555555555555", "{}\n");
        fx.AddSession("Users-me-proj", "66666666-6666-6666-6666-666666666666", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        // Pass the dashed form; the filter must normalize to dashless before matching.
        var got = await src.DiscoverAsync(Filters(filterSession: "55555555-5555-5555-5555-555555555555"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].SessionId).IsEqualTo("55555555555555555555555555555555");
    }

    [Test]
    public async Task discover_applies_cwd_filter_against_resolved_workspace_folder() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/match");
        fx.AddWorkspaceJson("hash-bbb", "file:///Users/me/dev/other");
        fx.AddSession("Users-me-dev-match", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "{}\n");
        fx.AddSession("Users-me-dev-other", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(filterCwd: "/Users/me/dev/match"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].SessionId).IsEqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    [Test]
    public async Task classify_marks_new_when_server_has_no_state() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        );
        using var client = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(client, minLines: 1), CancellationToken.None);

        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        // FilePath stays empty so ImportCommand routes Cursor through the
        // routed phase (ImportSessionAsync) instead of the Claude/Codex chain
        // worker. Transcript path lives in SourceMeta.
        await Assert.That(classified[0].FilePath).IsEqualTo("");
        await Assert.That((string)classified[0].SourceMeta!["TranscriptPath"]!).IsEqualTo(jsonl);
        await Assert.That(classified[0].TotalLines).IsEqualTo(3);
    }

    [Test]
    public async Task classify_keeps_file_path_empty_so_orchestrator_routes_to_ImportSessionAsync() {
        // Qodo P1 regression test: ImportCommand splits classifications into
        // file-based (chain worker, /hooks/session-start sans vendor suffix —
        // server defaults to claude) and routed (ImportSessionAsync —
        // Cursor's path). Non-empty FilePath misroutes Cursor sessions
        // through the Claude-shaped lifecycle.
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n{}\n");

        var       src     = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 0),
            CancellationToken.None
        );

        foreach (var c in classified) {
            await Assert.That(c.FilePath).IsEqualTo("");
        }
    }

    [Test]
    public async Task classify_marks_already_loaded_when_server_at_or_past_last_non_blank_line() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        // Three non-blank lines at indexes 0,1,2 → last_line_number=2 means fully loaded.
        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"last_line_number":2}""")
            }
        );
        using var client = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task classify_marks_partial_with_resume_from_when_server_mid_file() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"last_line_number":0}""")
            }
        );
        using var client = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(1);
    }

    [Test]
    public async Task classify_marks_too_short_below_min_lines() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n");
        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 5),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task classify_returns_probe_error_when_watermark_returns_5xx() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n"
        );
        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
    }

    [Test]
    public async Task import_session_posts_lifecycle_then_transcript_then_session_end() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        var outcome = await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(posted.Count).IsEqualTo(3);

        // Order matters: session-start before transcript, session-end after.
        await Assert.That(posted[0].Path).IsEqualTo("/hooks/session-start/cursor");
        await Assert.That(posted[1].Path).IsEqualTo("/hooks/transcript");
        await Assert.That(posted[2].Path).IsEqualTo("/hooks/session-end/cursor");

        var startNode = JsonNode.Parse(posted[0].Body)!;
        await Assert.That(startNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(startNode["hook_event_name"]!.GetValue<string>()).IsEqualTo("sessionStart");
        await Assert.That(startNode["workspace_roots"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("/Users/me/dev/proj");
        await Assert.That(startNode["transcript_path"]!.GetValue<string>()).IsEqualTo(jsonl);

        var transcriptNode = JsonNode.Parse(posted[1].Body)!;
        await Assert.That(transcriptNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(transcriptNode["vendor"]!.GetValue<string>()).IsEqualTo("cursor");
        await Assert.That(transcriptNode["lines"]!.AsArray().Count).IsEqualTo(3);

        var endNode = JsonNode.Parse(posted[2].Body)!;
        await Assert.That(endNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(endNode["hook_event_name"]!.GetValue<string>()).IsEqualTo("sessionEnd");
        await Assert.That(endNode["reason"]!.GetValue<string>()).IsEqualTo("historical-import");
    }

    [Test]
    public async Task import_session_populates_started_at_and_ended_at_from_file_times() {
        // AI-739: synthetic lifecycle hooks must carry the JSONL file's
        // creation/last-write time so the server records canonical
        // SessionStarted/SessionEnded with the real timestamps, not
        // import-time wall clock.
        //
        // Linux ext4 has no usable birth-time round-trip via .NET's File
        // APIs — SetCreationTimeUtc is a no-op, GetCreationTimeUtc returns
        // mtime — so on Linux started_at and ended_at collapse to the same
        // value. The test verifies the macOS/Windows contract; Linux gets
        // degraded-but-functional behavior in production.
        if (OperatingSystem.IsLinux()) return;

        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var created  = new DateTime(2026, 3, 14, 9, 27, 0, DateTimeKind.Utc);
        var modified = new DateTime(2026, 3, 14, 10, 27, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(jsonl, created);
        File.SetLastWriteTimeUtc(jsonl, modified);

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        var endNode   = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-end/cursor").Body)!;

        var startedAt = DateTimeOffset.Parse(startNode["started_at"]!.GetValue<string>());
        var endedAt   = DateTimeOffset.Parse(endNode["ended_at"]!.GetValue<string>());

        await Assert.That(startedAt.UtcDateTime).IsEqualTo(created);
        await Assert.That(endedAt.UtcDateTime).IsEqualTo(modified);
        await Assert.That((long)endNode["duration_ms"]!).IsEqualTo(3_600_000L); // 1h
    }

    [Test]
    public async Task import_session_returns_failed_when_session_start_post_fails() {
        // Reviewer P2a: lifecycle POST failure must hard-fail the import so
        // the user re-runs. Otherwise transcript success + lifecycle failure
        // leaves the session permanently lifecycle-less on the server.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();

        using var handler = new StubHandler(
            postCapture: (req, _) => {
                var path = req.RequestUri!.AbsolutePath;
                posted.Add(path);

                return path == "/hooks/session-start/cursor"
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = "cursor",
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
        // Transcript MUST NOT be posted when session-start failed —
        // otherwise the watermark advances and next-run sees AlreadyLoaded.
        await Assert.That(posted).DoesNotContain("/hooks/transcript");
    }

    [Test]
    public async Task import_session_returns_failed_when_session_end_post_fails() {
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            postCapture: (req, _) => req.RequestUri!.AbsolutePath == "/hooks/session-end/cursor"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = "cursor",
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
    }

    [Test]
    public async Task import_session_emits_lifecycle_only_for_already_loaded_status() {
        // Reviewer P2a (backfill case): re-running import on an already-loaded
        // Cursor session re-asserts lifecycle without resending transcript.
        // The orchestrator's routed-phase filter now includes AlreadyLoaded
        // for this exact case; ImportSessionAsync must short-circuit the
        // transcript batch but still emit session-start + session-end.
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();

        using var handler = new StubHandler(
            postCapture: (req, _) => {
                posted.Add(req.RequestUri!.AbsolutePath);

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
                TotalLines = 3,
                Vendor     = "cursor",
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");
        await Assert.That(posted).DoesNotContain("/hooks/transcript");
    }

    [Test]
    public async Task import_session_attaches_repository_from_detected_workspace_repo() {
        // AI-1152: the import/backfill path must attach a `repository` node to the
        // synthetic sessionStart so historical Cursor sessions emit RepositoryDetected
        // server-side and group under their repo (not just "All repos"). Detected from
        // the workspace folder via the repo detector (git remote parse).
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n{}\n");

        var src = new CursorImportSource(
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(
                new RepositoryPayload {
                    Owner     = "kurrent-io",
                    RepoName  = "kcap-server",
                    Branch    = "main",
                    RemoteUrl = "git@github.com:kurrent-io/kcap-server.git",
                }
            )
        );

        var posted = new List<(string Path, string Body)>();
        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        var repo      = startNode["repository"];
        await Assert.That(repo).IsNotNull();
        await Assert.That(repo!["owner"]!.GetValue<string>()).IsEqualTo("kurrent-io");
        await Assert.That(repo!["repo_name"]!.GetValue<string>()).IsEqualTo("kcap-server");
    }

    [Test]
    public async Task import_session_omits_repository_when_workspace_repo_undetected() {
        // Fail-open: non-git workspace (detector returns null) → no repository node,
        // session stays unattributed rather than erroring.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null)
        );

        var posted = new List<(string Path, string Body)>();
        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        await Assert.That(startNode["repository"]).IsNull();
    }

    [Test]
    public async Task subagent_child_is_imported_under_parent_agentsubsession_not_as_standalone() {
        // AI-1153: a Cursor subagent runs as its own session/transcript. When its first
        // user_query matches a parent's Task prompt, the importer must route it under the
        // parent's AgentSubsession stream (subagent-start + transcript-with-agent_id +
        // subagent-stop) and NOT emit a standalone session-start (which would create a
        // separate top-level session card).
        using var fx = new ProjectsDirFixture();

        const string prompt = "EXPLORE the repo and report back";
        var childUserText = System.Text.Json.JsonSerializer.Serialize("<user_query>\n" + prompt + "\n</user_query>");
        var taskPrompt    = System.Text.Json.JsonSerializer.Serialize(prompt);

        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"description\":\"d\",\"prompt\":" + taskPrompt + ",\"subagent_type\":\"generalPurpose\"}}]}}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" + childUserText + "}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var getHandler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var getClient  = new HttpClient(getHandler);

        var discovered  = await src.DiscoverAsync(Filters(), CancellationToken.None);
        var classified  = await src.ClassifyAsync(discovered, Ctx(getClient, minLines: 1), CancellationToken.None);

        const string parentId = "11111111111111111111111111111111";
        const string childId  = "22222222222222222222222222222222";

        var childClass = classified.Single(c => c.SessionId == childId);
        // Correlator stamped the parent link onto the child classification.
        await Assert.That((string)childClass.SourceMeta!["ParentSessionId"]!).IsEqualTo(parentId);

        var posted = new List<(string Path, string Body)>();
        using var postHandler = new StubHandler(postCapture: (req, body) => {
            posted.Add((req.RequestUri!.AbsolutePath, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var postClient = new HttpClient(postHandler);

        var outcome = await src.ImportSessionAsync(childClass, new ImportContext(postClient, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
        // Routed as a subagent: no standalone session-start/-end for the child.
        await Assert.That(posted.Select(p => p.Path)).DoesNotContain("/hooks/session-start/cursor");
        await Assert.That(posted.Select(p => p.Path)).DoesNotContain("/hooks/session-end/cursor");
        await Assert.That(posted.Select(p => p.Path)).Contains("/hooks/subagent-start");
        await Assert.That(posted.Select(p => p.Path)).Contains("/hooks/subagent-stop");
        await Assert.That(posted.Select(p => p.Path)).Contains("/hooks/transcript");

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/subagent-start").Body)!;
        await Assert.That(startNode["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(startNode["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
        await Assert.That(startNode["agent_type"]!.GetValue<string>()).IsEqualTo("generalPurpose");

        var transcriptNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/transcript").Body)!;
        await Assert.That(transcriptNode["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(transcriptNode["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
    }

    [Test]
    public async Task import_session_omits_workspace_roots_when_cwd_unresolved() {
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("unknown-workspace", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = "cursor",
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = null,
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted[0].Body)!;
        await Assert.That(startNode["workspace_roots"]).IsNull();
    }

    [Test]
    public async Task import_session_resumes_from_resume_line_for_partial_status() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId      = "11111111111111111111111111111111",
            FilePath       = "",
            EncodedCwd     = "",
            Meta           = new SessionMetadata(),
            Status         = ImportCommand.ClassificationStatus.Partial,
            ResumeFromLine = 1,
            Vendor         = "cursor",
            SourceMeta     = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
        };

        var outcome = await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Resumed);

        var transcriptPost = posted.First(p => p.Path == "/hooks/transcript");
        var node           = JsonNode.Parse(transcriptPost.Body)!;
        var lines          = node["lines"]!.AsArray();
        var lineNumbers    = node["line_numbers"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]!.GetValue<string>()).IsEqualTo("{\"b\":2}");
        await Assert.That((int)lineNumbers[0]!).IsEqualTo(1);
    }

    [Test]
    public async Task classify_sets_excluded_repo_key_when_workspace_repo_matches_excluded_list() {
        // Qodo bug #1 regression test: Cursor sessions must surface
        // ExcludedRepoKey so ImportCommand's auto-skip/prompt logic applies.
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/secret");

        fx.AddSession(
            "Users-me-dev-secret",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n"
        );

        var src = new CursorImportSource(
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(
                new RepositoryPayload { Owner = "acme", RepoName = "secret" }
            )
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            new ClassifyContext(
                client,
                "http://localhost",
                MinLines: 1,
                ExcludedRepos: new[] { "acme/secret" },
                ExcludedPaths: null
            ),
            CancellationToken.None
        );

        await Assert.That(classified[0].ExcludedRepoKey).IsEqualTo("acme/secret");
    }

    [Test]
    public async Task classify_does_not_invoke_repo_detection_when_excluded_repos_empty() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/foo");
        fx.AddSession("Users-me-dev-foo", "11111111-1111-1111-1111-111111111111", "{}\n");

        var detectorCalls = 0;

        var src = new CursorImportSource(
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => {
                detectorCalls++;

                return Task.FromResult<RepositoryPayload?>(null);
            }
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 0),
            CancellationToken.None
        );

        await Assert.That(detectorCalls).IsEqualTo(0);
    }

    [Test]
    public async Task classify_caches_repo_detection_per_workspace_across_sessions() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/shared");
        fx.AddSession("Users-me-dev-shared", "11111111-1111-1111-1111-111111111111", "{}\n");
        fx.AddSession("Users-me-dev-shared", "22222222-2222-2222-2222-222222222222", "{}\n");
        fx.AddSession("Users-me-dev-shared", "33333333-3333-3333-3333-333333333333", "{}\n");

        var detectorCalls = 0;

        var src = new CursorImportSource(
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => {
                detectorCalls++;

                return Task.FromResult<RepositoryPayload?>(new RepositoryPayload { Owner = "o", RepoName = "r" });
            }
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            new ClassifyContext(
                client,
                "http://localhost",
                MinLines: 0,
                ExcludedRepos: new[] { "o/r" },
                ExcludedPaths: null
            ),
            CancellationToken.None
        );

        await Assert.That(detectorCalls).IsEqualTo(1);
    }

    [Test]
    public async Task discover_cwd_filter_matches_case_insensitively_on_macos_and_windows() {
        // Qodo bug #2 regression test: cwd comparison must be case-insensitive
        // on case-insensitive filesystems. Skipped on Linux where Ordinal is correct.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;

        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/MyProj");
        fx.AddSession("Users-me-dev-MyProj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        // Caller passes a lower-cased cwd, e.g. from a shell tab-completion.
        var got = await src.DiscoverAsync(Filters(filterCwd: "/users/me/dev/myproj"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
    }

    [Test]
    public async Task discover_since_filter_uses_file_creation_time_not_last_write() {
        // Qodo P2 regression test: a session whose JSONL was created BEFORE
        // the cutoff but appended to AFTER it must still be excluded by
        // --since. Cursor JSONL has no in-band timestamps, so the file
        // creation time is the closest proxy for session-start.
        //
        // Linux ext4 doesn't expose btime through .NET's File APIs:
        // SetCreationTimeUtc is a silent no-op and GetCreationTimeUtc falls
        // back to mtime. This test exercises a macOS/Windows-only filesystem
        // contract; on Linux the production code degrades to gating on mtime,
        // which is documented but not asserted here.
        if (OperatingSystem.IsLinux()) return;

        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        // Set creation = 30 days ago, last write = today.
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        File.SetCreationTimeUtc(jsonl, thirtyDaysAgo);
        File.SetLastWriteTimeUtc(jsonl, DateTime.UtcNow);

        var src   = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var got   = await src.DiscoverAsync(Filters(since: since), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discover_marks_cwd_null_when_two_workspaces_encode_to_the_same_sanitized_key() {
        // Qodo bug #3 regression test: EncodeWorkspacePath is lossy
        // ("/foo/bar" and "/foo-bar" both → "foo-bar"); on collision we leave
        // cwd null rather than misattributing to one of them.
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-a", "file:///foo/bar");
        fx.AddWorkspaceJson("hash-b", "file:///foo-bar");
        fx.AddSession("foo-bar", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsNull();
    }

    static DiscoveryFilters Filters(string? filterCwd = null, string? filterSession = null, DateOnly? since = null, int minLines = 0) =>
        new(FilterCwd: filterCwd, FilterSession: filterSession, Since: since, MinLines: minLines);

    static ClassifyContext Ctx(HttpClient http, int minLines = 0) =>
        new(http, "http://localhost", minLines, ExcludedRepos: null, ExcludedPaths: null);

    sealed class ProjectsDirFixture : IDisposable {
        public string Root                { get; }
        public string ProjectsDir         => Path.Combine(Root, ".cursor", "projects");
        public string WorkspaceStorageDir => Path.Combine(Root, "workspaceStorage");

        public ProjectsDirFixture() {
            Root = Path.Combine(Path.GetTempPath(), $"kcap-cursor-walker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(ProjectsDir);
            Directory.CreateDirectory(WorkspaceStorageDir);
        }

        public string AddSession(string sanitized, string sessionId, string jsonlContent) {
            var dir = Path.Combine(ProjectsDir, sanitized, "agent-transcripts", sessionId);
            Directory.CreateDirectory(dir);
            var jsonl = Path.Combine(dir, sessionId + ".jsonl");
            File.WriteAllText(jsonl, jsonlContent);

            return jsonl;
        }

        public void AddWorkspaceJson(string hashDir, string folderUri) {
            var dir = Path.Combine(WorkspaceStorageDir, hashDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "workspace.json"), $$"""{"folder":"{{folderUri}}"}""");
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    sealed class StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage>?         getResponse = null,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture = null
        )
        : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get)
                return getResponse?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            return postCapture?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
