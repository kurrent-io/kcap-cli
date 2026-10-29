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

    [Test]
    public async Task ShowCursor_prefers_a_terminal_stderr() {
        var error  = new StringWriter();
        var output = new StringWriter();

        InteractiveLifetime.ShowCursor(error, errorRedirected: false, output, outputRedirected: false);

        await Assert.That(error.ToString()).IsEqualTo("\u001b[?25h");
        await Assert.That(output.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task ShowCursor_falls_back_to_a_terminal_stdout_when_stderr_is_redirected() {
        var error  = new StringWriter();
        var output = new StringWriter();

        InteractiveLifetime.ShowCursor(error, errorRedirected: true, output, outputRedirected: false);

        await Assert.That(error.ToString()).IsEqualTo("");
        await Assert.That(output.ToString()).IsEqualTo("\u001b[?25h");
    }

    [Test]
    public async Task ShowCursor_writes_nothing_when_both_streams_are_redirected() {
        var error  = new StringWriter();
        var output = new StringWriter();

        InteractiveLifetime.ShowCursor(error, errorRedirected: true, output, outputRedirected: true);

        await Assert.That(error.ToString() + output.ToString()).IsEqualTo("");
    }
}
