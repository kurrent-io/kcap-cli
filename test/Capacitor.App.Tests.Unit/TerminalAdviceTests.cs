using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

/// The cases are the installer's own failure messages, verbatim apart from their interpolated values.
public class TerminalAdviceTests {
    [Test]
    [Arguments(
        "Setup could not finish its commit (disk full). Nothing was removed; run kcap setup --local again to finish it.",
        "Setup could not finish its commit (disk full). Nothing was removed.")]
    [Arguments(
        "The local server at http://127.0.0.1:5123 did not confirm it left setup mode, even after a restart. The setup journal is kept; run kcap setup --local again to finish it.",
        "The local server at http://127.0.0.1:5123 did not confirm it left setup mode, even after a restart. The setup journal is kept.")]
    [Arguments(
        "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json). Fix or remove it, then run kcap setup --local again.",
        "The installation registry /Users/me/.local/kcap/registry.json cannot be read (bad json).")]
    [Arguments(
        "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked. The setup journal is kept; fix the configuration, then run kcap setup --local again.",
        "The profile \"default\" in /Users/me/.config/kcap could not be pointed at http://127.0.0.1:5123: locked. The setup journal is kept.")]
    [Arguments(
        "The download failed. The service could not be stopped. The setup journal is kept; the next kcap setup --local retries the cleanup.",
        "The download failed. The service could not be stopped. The setup journal is kept.")]
    [Arguments(
        "The setup journal /Users/me/.local/kcap/journal.json needs a newer kcap to finish. The setup journal /Users/me/.local/kcap/journal.json was left as it is; update kcap, then run kcap setup --local again.",
        "The setup journal /Users/me/.local/kcap/journal.json needs a newer kcap to finish. The setup journal /Users/me/.local/kcap/journal.json was left as it is.")]
    [Arguments(
        "The musl builds of kcap do not support a local installation.",
        "The musl builds of kcap do not support a local installation.")]
    public async Task Terminal_commands_are_dropped_and_the_cause_kept(string message, string expected) {
        var stripped = TerminalAdvice.Strip(message);

        await Assert.That(stripped).IsEqualTo(expected);
        await Assert.That(stripped!).DoesNotContain("kcap setup");
    }

    [Test]
    public async Task A_message_that_is_only_advice_leaves_nothing() {
        await Assert.That(TerminalAdvice.Strip("Fix the configuration, then run kcap setup --local again.")).IsNull();
    }
}
