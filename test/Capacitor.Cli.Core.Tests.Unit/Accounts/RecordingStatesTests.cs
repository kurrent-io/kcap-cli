using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class RecordingStatesTests {
    [Test]
    [Arguments(RecordingState.Recording, "recording")]
    [Arguments(RecordingState.Installed, "installed")]
    [Arguments(RecordingState.Broken, "broken")]
    [Arguments(RecordingState.NotWired, "not_wired")]
    public async Task Each_state_has_its_snake_case_token(RecordingState state, string token) =>
        await Assert.That(RecordingStates.Token(state)).IsEqualTo(token);
}
