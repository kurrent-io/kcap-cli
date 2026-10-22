using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class CommandLinePromptTests {
    /// <summary>Each argument costs its quoted length plus a separating space, so the boundary is
    /// exact: a budget-sized command line still fits.</summary>
    [Test]
    public async Task Overflows_only_past_the_budget() {
        var fits = new string('x', CommandLinePrompt.ArgumentBudget - 1);

        await Assert.That(CommandLinePrompt.Overflows([fits])).IsFalse();
        await Assert.That(CommandLinePrompt.Overflows([fits, "y"])).IsTrue();
    }

    [Test]
    public async Task Overflows_counts_the_quotes_an_argument_with_a_space_needs() {
        var spaced = "a " + new string('x', CommandLinePrompt.ArgumentBudget - 4);

        await Assert.That(CommandLinePrompt.Overflows([spaced])).IsTrue();
    }

    [Test]
    [Arguments("/review 42", true)]
    [Arguments("  /compact", true)]
    [Arguments("/plugin:command arg", true)]
    [Arguments("/compact\n", true)]
    [Arguments("/review 42\nprivate notes", false)]
    [Arguments("/Users/me/notes.md has the plan", false)]
    [Arguments("/ this is prose", false)]
    [Arguments("build it", false)]
    public async Task IsSlashCommand_recognises_a_command_name_not_a_path(string prompt, bool expected) {
        await Assert.That(CommandLinePrompt.IsSlashCommand(prompt)).IsEqualTo(expected);
    }
}
