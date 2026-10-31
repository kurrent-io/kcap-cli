using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeImportSourceTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task vendor_is_claude() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects, router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(src.Vendor).IsEqualTo(HarnessId.Claude);
    }

    [Test]
    public async Task supports_title_generation() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects, router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(src.SupportsTitleGeneration).IsTrue();
    }

    [Test]
    public async Task is_available_when_projects_dir_exists() {
        using var tmp = new TempDir();
        var src = new ClaudeImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(src.IsAvailable).IsTrue();
    }

    [Test]
    public async Task is_unavailable_when_projects_dir_missing() {
        using var missingDir = TempDir.WithPathTo("kcap-claude-source-missing", out var missing);
        var src     = new ClaudeImportSource(Config.Root, missing, router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    /// <summary>A title run as a Claude build that leaves the enqueued prompt empty records it, and a run
    /// carrying kcap's session id, are neither counted nor offered; the user's session beside them is, and
    /// every file stays on disk.</summary>
    [Test]
    public async Task discovery_drops_kcap_helper_runs_and_leaves_their_files() {
        using var tmp = new TempDir();
        var helperCwd = "/private/var/folders/x1/T/" + ClaudeHelperRun.WorkingDirName(Guid.NewGuid().ToString());
        var helperDir = tmp.CreateDir("-private-var-folders-x1-T-kcap-claude-run");
        var userId    = Guid.NewGuid().ToString();
        var helperId  = ClaudeHelperRun.NewSessionId();

        var emptyEnqueue = helperDir.CreateFile($"{Guid.NewGuid()}.jsonl", [
            """{"type":"queue-operation","operation":"enqueue","sessionId":"x"}""",
            """{"type":"queue-operation","operation":"dequeue","sessionId":"x"}""",
            $$$"""{"type":"user","cwd":"{{{helperCwd}}}","message":{"content":"Summarize: fix the login redirect"}}""",
        ]);
        var tagged = helperDir.CreateFile($"{helperId}.jsonl", [
            """{"type":"user","cwd":"/somewhere/else","message":{"content":"Summarize: fix the login redirect"}}""",
        ]);
        tmp.CreateDir("-home-me-src-app").CreateFile($"{userId}.jsonl", [
            """{"type":"user","cwd":"/home/me/src/app","message":{"content":"fix the login redirect"}}""",
        ]);

        var src   = new ClaudeImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var found = await src.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(found.Select(s => s.SessionId)).IsEquivalentTo([ImportCommand.NormalizeGuid(userId)]);
        await Assert.That(File.Exists(emptyEnqueue) && File.Exists(tagged)).IsTrue();
    }

    /// <summary>Naming a helper run by id finds nothing either: it is not the user's session to import.</summary>
    [Test]
    public async Task a_session_filter_naming_a_helper_run_finds_nothing() {
        using var tmp = new TempDir();
        var helperId = ClaudeHelperRun.NewSessionId();
        tmp.CreateDir("-tmp-kcap-claude-run").CreateFile($"{helperId}.jsonl", [
            """{"type":"user","cwd":"/tmp/x","message":{"content":"Summarize"}}""",
        ]);

        var src   = new ClaudeImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var found = await src.DiscoverAsync(new DiscoveryFilters(null, helperId, null, 0), CancellationToken.None);

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task import_session_async_throws_not_implemented() {
        var src = new ClaudeImportSource(Config.Root, new ClaudePaths(Home, null).Projects, router: new GitProviderRouter(), time: TimeProvider.System);
        var classification = new ImportCommand.SessionClassification {
            SessionId  = "abc",
            FilePath   = "/tmp/none",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
        };
        var ctx = new ImportContext(new HttpClient(), "http://localhost", ForcePrivate: false);

        var ex = await Assert.ThrowsAsync<NotImplementedException>(
            () => src.ImportSessionAsync(classification, ctx, CancellationToken.None)
        );

        await Assert.That(ex?.Message).Contains("ImportChainsAsync");
    }
}
