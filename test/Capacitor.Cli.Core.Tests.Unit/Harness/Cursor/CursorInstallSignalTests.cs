using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorInstallSignalTests {
    static CursorHarness Cur(string home) => CursorHarness.Over(new CursorPaths(new(home)));

    [Test]
    public async Task Installed_true_when_user_home_has_dot_cursor() {
        using var tmp = new TempDir();
        tmp.CreateDir(".cursor");

        await Assert.That(Cur(tmp.Path).Signals.HasUserData).IsTrue();
    }

    /// The Electron user dir is the second signal, and the path comes from the layout rather than
    /// from here — which dir it names is that host's business. Windows puts it under the real
    /// Roaming AppData, which no temp home can stand in for.
    [Test]
    public async Task Installed_true_when_this_hosts_electron_user_dir_exists() {
        Skip.When(OperatingSystem.IsWindows(), "the Windows Electron dir lies outside any temp home");

        using var tmp = new TempDir();
        var       cur = Cur(tmp.Path);

        Directory.CreateDirectory(cur.Paths.UserDir);

        await Assert.That(cur.Signals.HasUserData).IsTrue();
    }

    [Test]
    public async Task Installed_false_when_no_cursor_dirs_exist() {
        using var tmp = new TempDir();

        await Assert.That(Cur(tmp.Path).Signals.HasUserData).IsFalse();
    }
}
