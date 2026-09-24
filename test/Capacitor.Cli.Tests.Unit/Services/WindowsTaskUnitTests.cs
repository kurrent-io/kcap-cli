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
    public async Task TaskXml_is_well_formed_and_runs_the_wrapper_in_a_windowless_console() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\u\.config\kcap\daemon-service-laptop.cmd");
        XDocument.Parse(xml); // throws if malformed
        await Assert.That(xml).Contains("<Command>conhost.exe</Command>");
        await Assert.That(xml).Contains("<Arguments>--headless cmd.exe /d /s /v:off /c");
        await Assert.That(xml).Contains("daemon-service-laptop.cmd");
    }

    /// A logon trigger without a UserId means "any user" and needs an elevated token to register, so an
    /// ordinary `kcap daemon service install` was refused with "Access is denied".
    [Test]
    public async Task TaskXml_scopes_the_trigger_and_principal_to_the_installing_user() {
        var xml = XDocument.Parse(WindowsTaskUnit.TaskXml(Spec(), @"C:\k\daemon-service-laptop.cmd", @"CORP\a&b"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        await Assert.That(xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value).IsEqualTo(@"CORP\a&b");
        var principal = xml.Descendants(ns + "Principal").Single();
        await Assert.That(principal.Element(ns + "UserId")!.Value).IsEqualTo(@"CORP\a&b");
        await Assert.That(principal.Element(ns + "LogonType")!.Value).IsEqualTo("InteractiveToken");
        await Assert.That(principal.Element(ns + "RunLevel")!.Value).IsEqualTo("LeastPrivilege");
    }

    [Test]
    public async Task TaskXml_defaults_to_the_current_user() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\k\daemon-service-laptop.cmd");

        await Assert.That(xml).Contains($"<UserId>{Environment.UserDomainName}\\{Environment.UserName}</UserId>");
    }

    /// Task Scheduler never restarts a running action, so the wrapper restarts the daemon on any non-zero
    /// exit — negative crash codes included, which `if errorlevel 1` alone misses — and stops on exit 0.
    [Test]
    public async Task Wrapper_restarts_the_daemon_on_any_non_zero_exit() {
        var lines = WindowsTaskUnit.Wrapper(Spec()).Split("\r\n");
        var exec = Array.FindIndex(lines, l => l.StartsWith("\"C:\\kcap\\kcap-daemon.exe\"", StringComparison.Ordinal));

        await Assert.That(lines[exec - 1]).IsEqualTo(":run");
        await Assert.That(lines[(exec + 1)..(exec + 7)]).IsEquivalentTo(new[] {
            "if not errorlevel 0 goto restart",
            "if errorlevel 1 goto restart",
            "exit /b 0",
            ":restart",
            "ping -n 6 127.0.0.1 >nul",
            "goto run",
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
