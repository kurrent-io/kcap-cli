using System.Xml.Linq;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerTaskUnitTests {
    static LocalServerUnit Unit(string root = @"C:\Users\u\.kcap\local") =>
        new(@"C:\Users\u\.kcap\local\bin\current\kcap-server.exe", new LocalRoot(root), 51234);

    const string Wrapper = @"C:\Users\u\.config\kcap\kcap-local.cmd";

    [Test]
    public async Task The_wrapper_runs_the_server_into_its_log_and_relaunches_it_only_after_a_failure() {
        var unit  = Unit();
        var lines = LocalServerTaskUnit.Wrapper(unit).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines).Contains(@"set ""KCAP_LOCAL_ROOT=C:\Users\u\.kcap\local""");
        await Assert.That(lines).Contains(@"set ""Local__Port=51234""");
        await Assert.That(lines[Array.IndexOf(lines, ":run")..]).IsEquivalentTo(new[] {
            ":run",
            $@"""{unit.ServerBinary}"" >>""{unit.OutputLog}"" 2>&1",
            "if %ERRORLEVEL% EQU 0 exit /b 0",
            $@"call ""%SystemRoot%\System32\PING.EXE"" -n {WindowsTaskUnit.RelaunchPauseSeconds + 1} 127.0.0.1 >nul",
            "goto run",
        }, CollectionOrdering.Matching);
    }

    /// <summary>The owner is read back from the wrapper, so a root that needs escaping must survive the round trip.</summary>
    [Test]
    public async Task The_root_reads_back_from_the_wrapper_with_a_percent_in_it() {
        var env = WindowsTaskUnit.EnvFromWrapper(LocalServerTaskUnit.Wrapper(Unit(@"C:\Users\a%PATH%b\local")));

        await Assert.That(env[LocalRoot.EnvVar]).IsEqualTo(@"C:\Users\a%PATH%b\local");
    }

    [Test]
    public async Task A_value_the_wrapper_cannot_carry_is_refused_naming_setup() {
        var unit = Unit() with { Embedding = LocalEmbeddingSetting.Ollama("nomic\"&calc") };

        await Assert.That(() => LocalServerTaskUnit.Wrapper(unit)).Throws<InvalidOperationException>().WithMessageContaining("kcap setup --local");
    }

    [Test]
    public async Task The_task_runs_the_wrapper_headless_at_normal_priority() {
        var xml = LocalServerTaskUnit.TaskXml(Unit(), Wrapper, @"CORP\u");
        var ns  = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");

        await Assert.That(XDocument.Parse(xml).Descendants(ns + "Priority").Single().Value).IsEqualTo("5");
        await Assert.That(WindowsTaskUnit.WrapperFromTaskXml(xml)).IsEqualTo(Wrapper);
    }

    [Test]
    public async Task Last_result_is_read_past_a_quoted_comma() {
        const string row = "\"HOST\",\"\\kcap-local\",\"N/A\",\"Running\",\"Interactive only\",\"10/31/2026 9:00:00 AM\",\"267009\",\"u\","
                         + "\"conhost.exe --headless\",\"N/A\",\"kcap local server\",\"Enabled\",\"Disabled\",\"Stop On Battery Mode, No Start On Batteries\"";

        await Assert.That(LocalServerTaskUnit.LastResult(row + "\r\n")).IsEqualTo(LocalServerTaskUnit.RunningResult);
    }

    [Test]
    public async Task Only_the_task_at_the_library_root_is_listed() {
        var unit = Unit();

        await Assert.That(LocalServerTaskUnit.Listed("\"\\kcap-local-old\",\"N/A\",\"Ready\"\r\n\"\\Other\\kcap-local\",\"N/A\",\"Ready\"\r\n", unit)).IsFalse();
        await Assert.That(LocalServerTaskUnit.Listed("\"\\Other\",\"N/A\",\"Ready\"\r\n\"\\KCAP-LOCAL\",\"N/A\",\"Disabled\"\r\n", unit)).IsTrue();
    }
}
