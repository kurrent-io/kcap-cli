using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class WindowsPathProbeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static WindowsPathProbe Probe(string? machine, string? user, string? pathExt = ".COM;.EXE;.BAT;.CMD") =>
        new(() => machine, () => user, File.Exists, pathExt);

    [Test]
    public async Task Terminal_path_is_machine_then_user() {
        var path = await Probe(@"C:\Windows", @"C:\Users\me\bin").TerminalPathAsync(CancellationToken.None);

        await Assert.That(path).IsEqualTo(@"C:\Windows;C:\Users\me\bin");
    }

    [Test]
    public async Task Neither_scope_set_is_unknown_not_empty() {
        var probe = Probe(null, "");

        await Assert.That(await probe.TerminalPathAsync(CancellationToken.None)).IsNull();
        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsNull();
    }

    /// An npm install on Windows puts a `kcap.cmd` shim on PATH, never a bare `kcap` — PATHEXT is
    /// what makes that count.
    [Test]
    public async Task Finds_a_cmd_shim_through_pathext() {
        var npm = Tmp.CreateDir("npm");
        var shim = Tmp.CreateFile(Path.Combine("npm", "kcap.cmd"), "@echo off");

        var probe = Probe(Tmp.CreateDir("system"), npm);

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await probe.KcapPathAsync(CancellationToken.None)).IsEqualTo(shim);
    }

    [Test]
    public async Task First_directory_wins_and_exe_precedes_cmd() {
        var first = Tmp.CreateDir("first");
        var second = Tmp.CreateDir("second");
        Tmp.CreateFile(Path.Combine("first", "kcap.cmd"), "");
        var exe = Tmp.CreateFile(Path.Combine("first", "kcap.exe"), "");
        Tmp.CreateFile(Path.Combine("second", "kcap.exe"), "");

        var found = await Probe(first, second).KcapPathAsync(CancellationToken.None);

        await Assert.That(found).IsEqualTo(exe);
    }

    [Test]
    public async Task A_bare_kcap_without_an_extension_does_not_count() {
        var dir = Tmp.CreateDir("bin");
        Tmp.CreateFile(Path.Combine("bin", "kcap"), "");

        await Assert.That(await Probe(dir, null).KcapOnPathAsync(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Relative_and_quoted_entries_are_handled() {
        var dir = Tmp.CreateDir("quoted dir");
        Tmp.CreateFile(Path.Combine("quoted dir", "kcap.exe"), "");

        var probe = Probe(@"relative\bin", $"\"{dir}\"");

        await Assert.That(await probe.KcapOnPathAsync(CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Missing_pathext_falls_back_to_the_windows_default() {
        var dir = Tmp.CreateDir("bin");
        Tmp.CreateFile(Path.Combine("bin", "kcap.exe"), "");

        await Assert.That(await Probe(dir, null, pathExt: null).KcapOnPathAsync(CancellationToken.None)).IsTrue();
    }
}
