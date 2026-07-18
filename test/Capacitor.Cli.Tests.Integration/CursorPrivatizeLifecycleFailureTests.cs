using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// a lifecycle failure (subagent-stop/session-end) or a no-new-
/// content retry AFTER a Cursor session's content has already been posted must not permanently
/// bypass <c>--private</c>. Drives the real <c>ImportCommand.HandleImport</c> orchestrator (not
/// just <see cref="CursorImportSource.ImportSessionAsync"/> in isolation) against a stub server,
/// because the bug lived in the routed-loop's privatize-set bookkeeping in
/// <c>ImportCommand.cs</c>, not in <see cref="CursorImportSource"/> itself.
///
/// <para>
/// Before the fix, a Cursor session only entered <c>importedSessionIds</c> — the sole input to
/// the end-of-run <c>--private</c> PUT /visibility pass — when its own
/// <see cref="ImportOutcome"/> was Loaded/Resumed AND it wasn't a lifecycle-only replay
/// (<see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/>). A lifecycle POST failing AFTER
/// content had already persisted made <see cref="CursorImportSource.ImportSessionAsync"/> return
/// <see cref="ImportOutcome.Failed"/> — excluded. A later retry that read the session as
/// AlreadyLoaded-with-nothing-new was excluded too (lifecycle-only replay). Either way the
/// session's already-public content was never privatized, even on a fresh <c>--private</c> run.
/// </para>
///
/// <para>
/// The fix adds a separate, outcome-independent tracker
/// (<c>privateScopeSessionIds</c> in <c>ImportCommand.cs</c>) that captures every Cursor routed
/// classification touched under <c>--private</c> regardless of outcome, and unions it into the
/// privatize set — without touching <c>importedSessionIds</c>/the Done-grid counting (that
/// stays deferred, untouched).
/// </para>
/// </summary>
public class CursorPrivatizeLifecycleFailureTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly string         _tempDir = Directory.CreateTempSubdirectory("kcap-cursor-privatize-it").FullName;

    string ProjectsDir         => Path.Combine(_tempDir, ".cursor", "projects");
    string WorkspaceStorageDir => Path.Combine(_tempDir, "workspaceStorage");

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // On-disk Cursor session directories/files are named with dashes, but the CLI normalizes to
    // a dashless session id (CursorImportSource.NormalizeCursorSessionId) for every server call —
    // classification, hooks, and the visibility PUT all key on the dashless form.
    const string DirSessionId = "22222222-2222-2222-2222-222222222222";
    static readonly string SessionId = CursorImportSource.NormalizeCursorSessionId(DirSessionId);

    string WriteOneCursorSession() {
        Directory.CreateDirectory(ProjectsDir);

        var dir = Path.Combine(ProjectsDir, "no-workspace-match", "agent-transcripts", DirSessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DirSessionId + ".jsonl"), "{\"a\":1}\n{\"b\":2}\n");

        return ProjectsDir;
    }

    (string[] PutPaths, string[] PutBodies) VisibilityPuts() {
        var entries = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT" && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal))
            .ToArray();

        return (
            entries.Select(e => e.RequestMessage.Path).ToArray(),
            entries.Select(e => e.RequestMessage.Body!).ToArray()
        );
    }

    /// <summary>
    /// Case 1 (finding scenario 1): the child transcript POST succeeds (content persisted), but
    /// the subsequent session-end lifecycle POST FAILS. Old code: ImportSessionAsync returns
    /// Failed, the session never joins importedSessionIds, --private never privatizes it —
    /// content is left public forever. New code: the session is captured into
    /// privateScopeSessionIds unconditionally (before the outcome switch), so it's privatized
    /// regardless of the Failed outcome.
    /// </summary>
    [Test]
    public async Task private_run_privatizes_even_when_lifecycle_post_fails_after_content_persisted() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404)); // no watermark yet -> New
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)); // content persists
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500)); // lifecycle fails AFTER content persisted
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await ImportCommand.HandleImport(
            baseUrl: _server.Url!,
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, putBodies) = VisibilityPuts();
        await Assert.That(putPaths).Contains($"/api/sessions/{SessionId}/visibility");
        await Assert.That(putBodies.Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// Case 2 (finding scenario 2, the self-heal-on-retry contract): the session now classifies
    /// AlreadyLoaded (server already has every non-blank line) — the exact shape a retry sees
    /// after a prior run's content-then-lifecycle-failure. Old code: IsLifecycleOnlyRoutedReplay
    /// treats this as a no-op replay, excluded from importedSessionIds; a public session stays
    /// public even on repeated --private re-runs. New code: privatization no longer depends on
    /// that classification at all — the session is captured unconditionally.
    /// </summary>
    [Test]
    public async Task private_retry_run_privatizes_an_already_loaded_session_with_no_new_content() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}""")); // AlreadyLoaded
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await ImportCommand.HandleImport(
            baseUrl: _server.Url!,
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, putBodies) = VisibilityPuts();
        await Assert.That(putPaths).Contains($"/api/sessions/{SessionId}/visibility");
        await Assert.That(putBodies.Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// Case 3 (no-regression guard): a NON-private run must never call PUT /visibility, even
    /// though the session imports cleanly. Guards against the union-based fix accidentally
    /// widening privatization to plain imports.
    /// </summary>
    [Test]
    public async Task non_private_run_never_calls_set_visibility() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await ImportCommand.HandleImport(
            baseUrl: _server.Url!,
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: false
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, _) = VisibilityPuts();
        await Assert.That(putPaths.Length).IsEqualTo(0);
    }
}
