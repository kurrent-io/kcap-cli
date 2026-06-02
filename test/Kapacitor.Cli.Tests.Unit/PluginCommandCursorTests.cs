using Kapacitor.Cli.Commands;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandCursorTests {
    [Test]
    public async Task install_cursor_if_installed_noops_when_marker_absent() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(hooksPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_if_installed_short_circuits_on_same_version_marker() {
        using var tmp = new TempDir();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        File.WriteAllText(hooksPath, "{}");

        var marker = CursorHooksInstaller.ReadMarker(hooksPath);
        await Assert.That(marker).IsEqualTo(KapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(hooksPath)).IsEqualTo("{}");
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-pluginc-cursor-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
