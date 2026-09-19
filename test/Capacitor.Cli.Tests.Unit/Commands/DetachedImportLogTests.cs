using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DetachedImportLogTests {
    [Test]
    public async Task Absent_variable_means_no_detached_contract() {
        await Assert.That(DetachedImportLog.FromEnvironment(_ => null)).IsNull();
        await Assert.That(DetachedImportLog.FromEnvironment(v => v == DetachedImportLog.EnvVar ? "  " : null)).IsNull();
    }

    [Test]
    public async Task Visibility_rides_only_with_the_log_variable() {
        var env = new Dictionary<string, string?> {
            [DetachedImportLog.EnvVar]           = "/tmp/x.log",
            [DetachedImportLog.VisibilityEnvVar] = "private",
        };

        var contract = DetachedImportLog.FromEnvironment(k => env.GetValueOrDefault(k));

        await Assert.That(contract!.LogPath).IsEqualTo("/tmp/x.log");
        await Assert.That(contract.DefaultVisibility).IsEqualTo("private");
        await Assert.That(DetachedImportLog.FromEnvironment(k => k == DetachedImportLog.VisibilityEnvVar ? "private" : null)).IsNull();
    }

    [Test]
    public async Task Open_appends_to_the_existing_file_and_refuses_a_missing_one() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("import-run.log", "first\n");
        var contract = new DetachedImportLog(path, null);

        await using (var w = contract.Open()) await w.WriteAsync("second\n");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("first\nsecond\n");
        await Assert.That(() => new DetachedImportLog(tmp.PathTo("absent.log"), null).Open()).Throws<FileNotFoundException>();
    }
}
