using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit.Import;

public class ClaudeImportSourceTests {
    [Test]
    public async Task vendor_is_claude() {
        var src = new ClaudeImportSource();
        await Assert.That(src.Vendor).IsEqualTo("claude");
    }

    [Test]
    public async Task supports_title_generation() {
        var src = new ClaudeImportSource();
        await Assert.That(src.SupportsTitleGeneration).IsTrue();
    }

    [Test]
    public async Task is_available_when_projects_dir_exists() {
        var dir = Directory.CreateTempSubdirectory("kapacitor-claude-source-");
        try {
            var src = new ClaudeImportSource(dir.FullName);
            await Assert.That(src.IsAvailable).IsTrue();
        } finally {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task is_unavailable_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), "kapacitor-claude-source-missing-" + Guid.NewGuid().ToString("N"));
        var src     = new ClaudeImportSource(missing);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task import_session_async_throws_not_implemented() {
        var src = new ClaudeImportSource();
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

        await Assert.That(ex?.Message).Contains("E2");
    }
}
