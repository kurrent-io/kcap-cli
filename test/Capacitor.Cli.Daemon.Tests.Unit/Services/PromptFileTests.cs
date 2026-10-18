using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PromptFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>Each argument costs its quoted length plus a separating space, so the boundary is
    /// exact: a budget-sized command line still fits.</summary>
    [Test]
    public async Task Overflows_only_past_the_budget() {
        var fits = new string('x', PromptFile.ArgumentBudget - 1);

        await Assert.That(PromptFile.Overflows([fits])).IsFalse();
        await Assert.That(PromptFile.Overflows([fits, "y"])).IsTrue();
    }

    [Test]
    public async Task Overflows_counts_the_quotes_an_argument_with_a_space_needs() {
        var spaced = "a " + new string('x', PromptFile.ArgumentBudget - 4);

        await Assert.That(PromptFile.Overflows([spaced])).IsTrue();
    }

    [Test]
    public async Task Write_keeps_the_prompt_verbatim_and_Delete_removes_its_directory() {
        const string prompt = "line one\nline \"two\" & %PATH%\n";

        var path = PromptFile.Write(Tmp.Path, "agent-1", prompt);

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(prompt);

        PromptFile.Delete(path);

        await Assert.That(Directory.Exists(Path.GetDirectoryName(path))).IsFalse();
    }

    /// <summary>The agent id crosses the wire unconstrained, and Delete removes the directory
    /// recursively, so an id must never name a directory outside the root.</summary>
    [Test]
    public async Task Write_keeps_an_agent_id_with_a_parent_segment_under_the_root() {
        var root = Tmp.PathTo("prompts");

        var path = PromptFile.Write(root, "../victim", "x");

        await Assert.That(Path.GetDirectoryName(Path.GetDirectoryName(path))).IsEqualTo(root);
        await Assert.That(Directory.Exists(Tmp.PathTo("victim"))).IsFalse();
    }

    [Test]
    [Arguments("/review 42", true)]
    [Arguments("  /compact", true)]
    [Arguments("/plugin:command arg", true)]
    [Arguments("/Users/me/notes.md has the plan", false)]
    [Arguments("/ this is prose", false)]
    [Arguments("build it", false)]
    public async Task IsSlashCommand_recognises_a_command_name_not_a_path(string prompt, bool expected) {
        await Assert.That(PromptFile.IsSlashCommand(prompt)).IsEqualTo(expected);
    }

    [Test]
    public async Task Sweep_removes_every_prompt_left_under_the_root() {
        var root = Tmp.PathTo("prompts");
        PromptFile.Write(root, "agent-1", "x");

        PromptFile.Sweep(root);

        await Assert.That(Directory.Exists(root)).IsFalse();
    }
}
