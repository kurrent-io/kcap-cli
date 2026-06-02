using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

public class CursorPathsIsInstalledTests {
    [Test]
    public async Task IsInstalled_true_when_user_home_has_dot_cursor() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".cursor"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_macos_user_dir_exists() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "Library", "Application Support", "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.MacOs, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_linux_config_user_dir_exists() {
        using var tmp = new TempHome();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".config", "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_windows_appdata_user_dir_exists() {
        using var tmp = new TempHome();
        var appData = Path.Combine(tmp.Path, "AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(appData, "Cursor", "User"));
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Windows, appData)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_no_cursor_dirs_exist() {
        using var tmp = new TempHome();
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.Linux, appData: null)).IsFalse();
        await Assert.That(CursorPaths.IsInstalled(tmp.Path, OsPlatform.MacOs, appData: null)).IsFalse();
    }

    [Test]
    public async Task UserHooksJson_is_dot_cursor_hooks_json_under_home() {
        var resolved = CursorPaths.UserHooksJson(home: "/tmp/h");
        await Assert.That(resolved).IsEqualTo("/tmp/h/.cursor/hooks.json");
    }

    [Test]
    public async Task SpoolDir_is_dot_cursor_kapacitor_pending_under_home() {
        var resolved = CursorPaths.SpoolDir(home: "/tmp/h");
        await Assert.That(resolved).IsEqualTo("/tmp/h/.cursor/kapacitor-pending");
    }

    sealed class TempHome : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kapacitor-cursor-paths-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempHome() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
