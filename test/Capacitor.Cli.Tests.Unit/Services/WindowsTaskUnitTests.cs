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
    /// loop that only a clean exit leaves, and every other exit — a requested restart too — pauses first, so a
    /// daemon that keeps exiting cannot spin.</summary>
    [Test]
    public async Task Wrapper_relaunches_the_daemon_until_it_exits_cleanly() {
        var lines = WindowsTaskUnit.Wrapper(Spec()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var loop  = Array.IndexOf(lines, ":run");
        var exec  = Array.FindIndex(lines, l => l.StartsWith("\"C:\\kcap\\kcap-daemon.exe\"", StringComparison.Ordinal));

        await Assert.That(loop).IsGreaterThan(0);
        await Assert.That(lines[loop - 1]).IsEqualTo("set \"ERRORLEVEL=\"");
        await Assert.That(lines[(loop + 1)..]).IsEquivalentTo(new[] {
            lines[exec],
            "if %ERRORLEVEL% EQU 0 exit /b 0",
            $"call \"%SystemRoot%\\System32\\PING.EXE\" -n {WindowsTaskUnit.RelaunchPauseSeconds + 1} 127.0.0.1 >nul",
            "goto run",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>`if errorlevel N` means "at least N", which a crash's negative exit code fails; and `timeout`
    /// aborts when stdin is redirected. Either would end or spin the loop.</summary>
    [Test]
    public async Task Wrapper_tests_for_an_exact_zero_and_sleeps_with_ping() {
        var wrapper = WindowsTaskUnit.Wrapper(Spec());

        await Assert.That(wrapper.Contains("if errorlevel", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(wrapper.Contains("if not errorlevel", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(wrapper.Contains("timeout", StringComparison.OrdinalIgnoreCase)).IsFalse();
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
