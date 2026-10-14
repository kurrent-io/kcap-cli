using System.Text;
using Capacitor.Cli.Daemon.Harness.Muse;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

public class MuseMspProcessTests {
    [Test]
    public async Task One_line_longer_than_the_cap_is_cut_at_it() {
        var diagnostics = new StringBuilder();

        MuseMspProcess.AppendCapped(diagnostics, new string('x', MuseMspProcess.DiagnosticsCap * 3));

        await Assert.That(diagnostics.Length).IsEqualTo(MuseMspProcess.DiagnosticsCap);
    }

    [Test]
    public async Task Lines_after_the_cap_is_reached_are_dropped() {
        var diagnostics = new StringBuilder();

        MuseMspProcess.AppendCapped(diagnostics, "first");
        MuseMspProcess.AppendCapped(diagnostics, new string('x', MuseMspProcess.DiagnosticsCap));
        MuseMspProcess.AppendCapped(diagnostics, "late");

        await Assert.That(diagnostics.Length).IsEqualTo(MuseMspProcess.DiagnosticsCap);
        await Assert.That(diagnostics.ToString()).StartsWith("first\n");
        await Assert.That(diagnostics.ToString()).DoesNotContain("late");
    }
}
