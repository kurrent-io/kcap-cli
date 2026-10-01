using System.Xml.Linq;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class WindowsTaskUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, @"C:\kcap\kcap-daemon.exe", @"C:\Users\u\.config\kcap\daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = @"C:\bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

    [Test]
    public async Task TaskName_is_per_id() {
        await Assert.That(WindowsTaskUnit.TaskName("laptop")).IsEqualTo("kcap-daemon-laptop");
    }

    [Test]
    public async Task Wrapper_sets_env_and_execs_daemon() {
        var cmd = WindowsTaskUnit.Wrapper(Spec());
        await Assert.That(cmd).Contains("set \"PATH=C:\\bin\"");
        await Assert.That(cmd).Contains("set \"KCAP_PROFILE=work\"");
        // Every value on the exec line is quoted now, not only the paths: cmd treats & | < > ( ) ^ as live
        // metacharacters outside quotes, so an unquoted argument was a command-injection surface.
        await Assert.That(cmd).Contains("\"C:\\kcap\\kcap-daemon.exe\" --name \"laptop\" --log-file \"C:\\Users\\u\\.config\\kcap\\daemon-laptop.log\" \"--max-agents\" \"8\"");
    }

    /// <summary>Task Scheduler never relaunches on an exit code, so the wrapper must: the daemon runs inside a
    /// loop that only a clean exit leaves.</summary>
    [Test]
    public async Task Wrapper_relaunches_the_daemon_until_it_exits_cleanly() {
        var lines = WindowsTaskUnit.Wrapper(Spec()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var loop  = Array.IndexOf(lines, ":run");
        var exec  = Array.FindIndex(lines, l => l.StartsWith("\"C:\\kcap\\kcap-daemon.exe\"", StringComparison.Ordinal));

        await Assert.That(loop).IsGreaterThan(0);
        await Assert.That(exec).IsEqualTo(loop + 1);
        await Assert.That(lines[exec + 1]).IsEqualTo("set \"KCAP_WRAPPER_EXIT=%ERRORLEVEL%\"");
        await Assert.That(lines[exec + 2]).IsEqualTo("if \"%KCAP_WRAPPER_EXIT%\"==\"0\" exit /b 0");
        await Assert.That(lines[^1]).IsEqualTo("goto run");
    }

    /// <summary>A requested restart (after an update) relaunches straight away and forgets earlier failures.</summary>
    [Test]
    public async Task Wrapper_relaunches_a_requested_restart_without_pausing() {
        var lines = WindowsTaskUnit.Wrapper(Spec()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines).Contains($"if \"%KCAP_WRAPPER_EXIT%\"==\"{Capacitor.Cli.Core.ExitCodes.RestartRequested}\" (set \"KCAP_WRAPPER_FAILURES=0\" & goto run)");
    }

    /// <summary>Any other exit pauses before relaunching — longer once failures repeat — and never gives up:
    /// a task the wrapper leaves stays down until the next logon.</summary>
    [Test]
    public async Task Wrapper_pauses_longer_once_failures_repeat() {
        var lines = WindowsTaskUnit.Wrapper(Spec()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var pause = lines.Single(l => l.StartsWith("if %KCAP_WRAPPER_FAILURES%", StringComparison.Ordinal));

        await Assert.That(lines).Contains("set /a KCAP_WRAPPER_FAILURES+=1 >nul");
        await Assert.That(pause).IsEqualTo(
            $"if %KCAP_WRAPPER_FAILURES% GEQ {WindowsTaskUnit.RepeatedFailures} (ping -n {WindowsTaskUnit.LongPauseSeconds + 1} 127.0.0.1 >nul) else (ping -n {WindowsTaskUnit.ShortPauseSeconds + 1} 127.0.0.1 >nul)");
        await Assert.That(lines.Any(l => l.Contains("timeout", StringComparison.OrdinalIgnoreCase))).IsFalse();
        await Assert.That(lines.Any(l => l.StartsWith("exit", StringComparison.OrdinalIgnoreCase) && l != "exit /b 0")).IsFalse();
    }

    [Test]
    public async Task Wrapper_doubles_percent_in_values() {
        var spec = Spec() with { Environment = new Dictionary<string, string> { ["X"] = "50%done" } };
        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("set \"X=50%%done\"");
    }

    [Test]
    public async Task BinaryFromWrapper_extracts_daemon_path_not_wrapper() {
        var bin = WindowsTaskUnit.BinaryFromWrapper(WindowsTaskUnit.Wrapper(Spec()));
        await Assert.That(bin).IsEqualTo(@"C:\kcap\kcap-daemon.exe");
    }

    [Test]
    public async Task BinaryFromWrapper_unescapes_doubled_percent() {
        var spec = Spec() with { DaemonBinaryPath = @"C:\dir%x\kcap-daemon.exe" };
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(WindowsTaskUnit.Wrapper(spec)))
            .IsEqualTo(@"C:\dir%x\kcap-daemon.exe");
    }

    [Test]
    public async Task TaskXml_is_well_formed_and_runs_cmd_wrapper() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\u\.config\kcap\daemon-service-laptop.cmd");
        XDocument.Parse(xml); // throws if malformed
        await Assert.That(xml).Contains("<Command>cmd.exe</Command>");
        await Assert.That(xml).Contains("/c");
        await Assert.That(xml).Contains("daemon-service-laptop.cmd");
        await Assert.That(xml).Contains("<LogonTrigger>");
    }
}
