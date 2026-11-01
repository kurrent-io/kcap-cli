using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>The days an import's background work takes round up, and an allowance the server cannot have meant gives
/// no estimate</summary>
public class IntelligenceSetupResponseTests {
    [Test]
    [Arguments(200, 1, 1)]
    [Arguments(200, 200, 1)]
    [Arguments(200, 201, 2)]
    [Arguments(200, 0, 0)]
    [Arguments(0, 10, null)]
    public async Task Days_round_up_at_the_daily_allowance(int perDay, int sessions, int? days) =>
        await Assert.That(new IntelligenceSetupResponse("none", null, null, false, perDay, []).DaysFor(sessions)).IsEqualTo(days);
}
