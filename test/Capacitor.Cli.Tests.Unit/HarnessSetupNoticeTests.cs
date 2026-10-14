namespace Capacitor.Cli.Tests.Unit;

public class HarnessSetupNoticeTests {
    [Test]
    [Arguments("mcp")]
    [Arguments("watch")]
    [Arguments("daemon")]
    [Arguments("muse-assistant")]
    public async Task Machine_facing_commands_never_get_the_setup_notice(string command) {
        await Assert.That(HarnessSetupNotice.ShouldNotify(command)).IsFalse();
    }

    [Test]
    public async Task A_human_command_still_gets_it() {
        await Assert.That(HarnessSetupNotice.ShouldNotify("recap")).IsTrue();
    }
}
