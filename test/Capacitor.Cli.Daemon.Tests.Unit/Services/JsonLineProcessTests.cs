using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class JsonLineProcessTests {
    [Test]
    public async Task One_line_longer_than_the_cap_is_cut_at_it() {
        var diagnostics = new StringBuilder();

        JsonLineProcess.AppendCapped(diagnostics, new string('x', JsonLineProcess.DiagnosticsCap * 3));

        await Assert.That(diagnostics.Length).IsEqualTo(JsonLineProcess.DiagnosticsCap);
    }

    [Test]
    public async Task Lines_after_the_cap_is_reached_are_dropped() {
        var diagnostics = new StringBuilder();

        JsonLineProcess.AppendCapped(diagnostics, "first");
        JsonLineProcess.AppendCapped(diagnostics, new string('x', JsonLineProcess.DiagnosticsCap));
        JsonLineProcess.AppendCapped(diagnostics, "late");

        await Assert.That(diagnostics.Length).IsEqualTo(JsonLineProcess.DiagnosticsCap);
        await Assert.That(diagnostics.ToString()).StartsWith("first\n");
        await Assert.That(diagnostics.ToString()).DoesNotContain("late");
    }
}
