using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

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

    static CursorPaths MakePaths(string globalStateDb) =>
        new(
            UserDir:             "/tmp/none",
            WorkspaceStorageDir: "/tmp/none/workspaceStorage",
            GlobalStateDb:       globalStateDb);
}
