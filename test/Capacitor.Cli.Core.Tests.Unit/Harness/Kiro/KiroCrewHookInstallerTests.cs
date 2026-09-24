using System.Diagnostics;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class KiroCrewHookInstallerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Script => Tmp.PathTo("hooks", "kcap-spawn.sh");

    /// <summary>Crew skips a script without a recognised <c># event:</c> header in its first five
    /// lines and defaults an unheaded one to <c>preToolUse</c>.</summary>
    [Test]
    public async Task Script_declares_agentSpawn_in_the_header_Crew_scans() {
        var head = KiroCrewHookInstaller.Render("/opt/kcap/bin").Split('\n').Take(5).ToArray();

        await Assert.That(head[0]).IsEqualTo("#!/bin/sh");
        await Assert.That(head).Contains("# event: agentSpawn");
    }

    [Test]
    public async Task Install_writes_an_executable_script_and_is_idempotent() {
        if (OperatingSystem.IsWindows()) return;

        await Assert.That(KiroCrewHookInstaller.Install(Script, "/opt/kcap/bin")).IsEqualTo(KiroCrewHookInstaller.Outcome.Written);
        await Assert.That(File.GetUnixFileMode(Script).HasFlag(UnixFileMode.UserExecute)).IsTrue();
        await Assert.That(KiroCrewHookInstaller.IsInstalled(Script)).IsTrue();

        await Assert.That(KiroCrewHookInstaller.Install(Script, "/opt/kcap/bin")).IsEqualTo(KiroCrewHookInstaller.Outcome.Unchanged);
        await Assert.That(KiroCrewHookInstaller.Install(Script, "/usr/local/bin")).IsEqualTo(KiroCrewHookInstaller.Outcome.Written);
    }

    [Test]
    public async Task Remove_leaves_a_script_kcap_did_not_write() {
        Directory.CreateDirectory(Path.GetDirectoryName(Script)!);
        await File.WriteAllTextAsync(Script, "#!/bin/sh\n# event: agentSpawn\necho mine\n");

        await Assert.That(KiroCrewHookInstaller.IsInstalled(Script)).IsFalse();
        await Assert.That(KiroCrewHookInstaller.Remove(Script)).IsEqualTo(KiroCrewHookInstaller.Outcome.Unchanged);
        await Assert.That(File.Exists(Script)).IsTrue();
    }

    [Test]
    public async Task Remove_deletes_kcaps_script() {
        if (OperatingSystem.IsWindows()) return;

        KiroCrewHookInstaller.Install(Script, null);

        await Assert.That(KiroCrewHookInstaller.Remove(Script)).IsEqualTo(KiroCrewHookInstaller.Outcome.Removed);
        await Assert.That(File.Exists(Script)).IsFalse();
    }

    /// <summary>Crew's PATH need not hold <c>kcap</c>, so the install-time directory must still reach it,
    /// and the hook payload must arrive on the real hook's STDIN.</summary>
    [Test]
    public async Task Script_reaches_kcap_from_the_install_time_directory_with_stdin_intact() {
        if (OperatingSystem.IsWindows()) return;

        var bin      = Tmp.PathTo("bin");
        var captured = Tmp.PathTo("captured.txt");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "kcap");
        await File.WriteAllTextAsync(fake, $"#!/bin/sh\n{{ echo \"$@\"; cat; }} > '{captured}'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        KiroCrewHookInstaller.Install(Script, bin);

        var psi = new ProcessStartInfo(Script) { RedirectStandardInput = true, UseShellExecute = false };
        psi.Environment["PATH"] = "/usr/bin:/bin";

        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync("""{"hook_event_name":"agentSpawn"}""");
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        await Assert.That(process.ExitCode).IsEqualTo(0);
        var lines = await File.ReadAllLinesAsync(captured);
        await Assert.That(lines[0]).IsEqualTo("hook --kiro --event agentSpawn");
        await Assert.That(lines[1]).IsEqualTo("""{"hook_event_name":"agentSpawn"}""");
    }

    [Test]
    public async Task Install_leaves_a_script_kcap_did_not_write() {
        Directory.CreateDirectory(Path.GetDirectoryName(Script)!);
        await File.WriteAllTextAsync(Script, "#!/bin/sh\necho mine\n");

        await Assert.That(KiroCrewHookInstaller.Install(Script, "/opt/kcap/bin")).IsEqualTo(KiroCrewHookInstaller.Outcome.Unowned);
        await Assert.That(await File.ReadAllTextAsync(Script)).IsEqualTo("#!/bin/sh\necho mine\n");
    }

    /// <summary>A refresh must tell a script the user deleted from one never installed.</summary>
    [Test]
    public async Task A_deleted_script_reads_as_removed_until_kcap_removes_its_record() {
        if (OperatingSystem.IsWindows()) return;

        await Assert.That(KiroCrewHookInstaller.WasRemoved(Script)).IsFalse();

        KiroCrewHookInstaller.Install(Script, null);
        File.Delete(Script);
        await Assert.That(KiroCrewHookInstaller.WasRemoved(Script)).IsTrue();

        KiroCrewHookInstaller.Remove(Script);
        await Assert.That(KiroCrewHookInstaller.WasRemoved(Script)).IsFalse();
    }

    /// <summary>The install-time directory is data to the shell: a <c>$</c> or quote in it neither runs
    /// nor breaks the lookup.</summary>
    [Test]
    public async Task Script_treats_the_install_time_directory_as_data() {
        if (OperatingSystem.IsWindows()) return;

        var bin      = Tmp.PathTo("it's $(touch pwned) `x`");
        var captured = Tmp.PathTo("captured.txt");
        Directory.CreateDirectory(bin);
        var fake = Path.Combine(bin, "kcap");
        await File.WriteAllTextAsync(fake, $"#!/bin/sh\necho ran > '{captured}'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        KiroCrewHookInstaller.Install(Script, bin);

        var psi = new ProcessStartInfo(Script) { RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = Tmp.Path };
        psi.Environment["PATH"] = "/usr/bin:/bin";

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        await process.WaitForExitAsync();

        await Assert.That(process.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(captured)).IsTrue();
        await Assert.That(File.Exists(Tmp.PathTo("pwned"))).IsFalse();
    }
}
