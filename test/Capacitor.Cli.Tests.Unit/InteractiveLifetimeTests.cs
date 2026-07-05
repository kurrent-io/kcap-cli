namespace Capacitor.Cli.Tests.Unit;

public class InteractiveLifetimeTests {
    [Test]
    [Arguments("setup")]
    [Arguments("login")]
    [Arguments("profile")]
    [Arguments("use")]
    [Arguments("import")]
    [Arguments("uninstall")]
    public async Task IsInteractiveCommand_true_for_prompting_commands(string command) {
        await Assert.That(InteractiveLifetime.IsInteractiveCommand(command)).IsTrue();
    }

    [Test]
    // watch/daemon manage their own lifetime; hook/mcp are non-interactive.
    [Arguments("watch")]
    [Arguments("daemon")]
    [Arguments("hook")]
    [Arguments("mcp")]
    [Arguments("recap")]
    [Arguments("status")]
    [Arguments("")]
    public async Task IsInteractiveCommand_false_for_non_prompting_commands(string command) {
        await Assert.That(InteractiveLifetime.IsInteractiveCommand(command)).IsFalse();
    }
}
