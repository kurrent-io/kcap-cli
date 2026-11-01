using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A stop that sets off others keeps the cause that started it.</summary>
public class StopCauseTests {
    [Test]
    public async Task The_first_noted_cause_wins() {
        var cause = new StopCause();

        cause.Note("a local stop request (agents=end)");
        cause.Note("received SIGTERM");

        await Assert.That(cause.Reason).IsEqualTo("a local stop request (agents=end)");
    }
}
