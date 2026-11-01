using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>The desktop app reads <c>kcap local status --json</c> back into the reading the CLI made; anything it
/// cannot read is no answer, so the app falls back to its own reading rather than guessing.</summary>
public class LocalStatusJsonTests {
    public static IEnumerable<LocalServerCondition> Conditions() => Enum.GetValues<LocalServerCondition>();

    [Test]
    [MethodDataSource(nameof(Conditions))]
    public async Task Every_condition_reads_back_as_itself(LocalServerCondition condition) {
        var line = LocalStatusJson.From(false, "Not running.", ["a note"], new(condition, 9473)).Render();

        await Assert.That(LocalStatusJson.Parse(line)).IsEqualTo(new LocalServerReading(condition, 9473));
    }

    [Test]
    public async Task A_taken_port_keeps_its_holder_and_answer() {
        var reading = new LocalServerReading(LocalServerCondition.PortTaken, 9473, new PortHolder(4728, "powershell"), "a program that does not speak HTTP");

        var line = LocalStatusJson.From(false, "Stopped, and port 9473 is taken by another process.", [], reading).Render();

        await Assert.That(line).Contains("\"condition\":\"port_taken\"").And.Contains("\"holder_pid\":4728");
        await Assert.That(LocalStatusJson.Parse(line)).IsEqualTo(reading);
    }

    [Test]
    [Arguments("")]
    [Arguments("Usage: kcap local <subcommand>")]
    [Arguments("{\"ready\":false,\"headline\":\"x\",\"condition\":\"sleeping\",\"notes\":[]}")]
    [Arguments("{\"ready\":false,\"headline\":\"x\",\"notes\":[]}")]
    public async Task Output_that_is_not_a_known_status_is_no_answer(string stdout) {
        await Assert.That(LocalStatusJson.Parse(stdout)).IsNull();
    }
}
