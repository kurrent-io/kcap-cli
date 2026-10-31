using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// <summary>
/// The roots <see cref="DesktopAppProbe.FromEnvironment"/> picks on this host. Only the user-writable
/// ones are exercised: <c>/Applications</c> and Program Files are left alone.
/// </summary>
public class DesktopAppProbeTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Finds_an_entry_under_each_macOS_user_root() {
        Skip.Unless(OperatingSystem.IsMacOS(), "macOS roots");

        Home.CreateDir("Applications", "Probe.app");
        Home.CreateDir("Library", "Application Support", "ProbeData");

        var probe = DesktopAppProbe.FromEnvironment(Home);

        await Assert.That(probe.Finds("Probe.app")).IsTrue();
        await Assert.That(probe.Finds("ProbeData")).IsTrue();
        await Assert.That(probe.Finds("Absent.app")).IsFalse();
    }

    /// Local AppData comes from the OS, not the home, so the entry goes in the real one.
    [Test]
    public async Task Finds_an_entry_under_Windows_local_app_data() {
        Skip.Unless(OperatingSystem.IsWindows(), "Windows roots");

#pragma warning disable RS0030 // the real known folder is what is under test
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#pragma warning restore RS0030
        var entry = $"kcap-probe-{Guid.NewGuid():N}";
        var path  = Directory.CreateDirectory(Path.Combine(local, entry)).FullName;

        try {
            await Assert.That(DesktopAppProbe.FromEnvironment(Home).Finds(entry)).IsTrue();
        } finally {
            Directory.Delete(path, recursive: true);
        }
    }

    /// Linux Electron data shares ~/.config with CLI config kcap writes, so nothing there counts.
    [Test]
    public async Task Linux_has_no_root() {
        Skip.Unless(OperatingSystem.IsLinux(), "Linux roots");

        Home.CreateDir(".config", "Cursor");

        await Assert.That(DesktopAppProbe.FromEnvironment(Home).Finds("Cursor")).IsFalse();
    }

    [Test]
    public async Task Searching_finds_a_file_or_a_directory_under_any_root() {
        using var a = new TempDir();
        using var b = new TempDir();
        a.CreateDir("App.app");
        b.CreateFile("cli", "");

        var probe = DesktopAppProbe.Searching(a.Path, b.Path);

        await Assert.That(probe.Finds("App.app")).IsTrue();
        await Assert.That(probe.Finds("cli")).IsTrue();
        await Assert.That(probe.Finds("missing")).IsFalse();
        await Assert.That(DesktopAppProbe.None.Finds("App.app")).IsFalse();
    }

    [Test]
    public async Task Directories_lists_the_entry_under_each_root_that_has_it_in_root_order() {
        using var a = new TempDir();
        using var b = new TempDir();
        using var c = new TempDir();
        a.CreateDir("Data");
        b.CreateFile("Data", "a file, not a directory");
        var hit = c.CreateDir("Data");

        var dirs = DesktopAppProbe.Searching(c.Path, b.Path, a.Path).Directories("Data").ToList();

        await Assert.That(dirs).IsEquivalentTo([(string)hit, a.PathTo("Data")]);
        await Assert.That(dirs[0]).IsEqualTo((string)hit);
    }
}
