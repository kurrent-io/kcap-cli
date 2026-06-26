using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeImportLedgerTests {
    [Test]
    public async Task records_and_reports_complete_only_for_matching_line_count_and_server() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "ledger.json");
        const string url = "https://srv.example";

        var ledger = OpenCodeImportLedger.Load(path);
        await Assert.That(ledger.IsComplete(url, "ses_x", lineCount: 10)).IsFalse();

        ledger.MarkComplete(url, "ses_x", lineCount: 10);
        ledger.Save();

        var reloaded = OpenCodeImportLedger.Load(path);
        await Assert.That(reloaded.IsComplete(url, "ses_x", 10)).IsTrue();
        await Assert.That(reloaded.IsComplete(url, "ses_x", 11)).IsFalse();
        await Assert.That(reloaded.IsComplete("https://other", "ses_x", 10)).IsFalse();
        await Assert.That(reloaded.IsComplete(url, "ses_y", 10)).IsFalse();
    }

    [Test]
    public async Task load_tolerates_missing_or_corrupt_file() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "nope.json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", 1)).IsFalse();
        await File.WriteAllTextAsync(path, "{ not json");
        await Assert.That(OpenCodeImportLedger.Load(path).IsComplete("u", "s", 1)).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory("kcap-ledger").FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
