using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Pure: no dispatcher, so no session constraint.
public class ToolInspectionTests {
    static ToolInspectionSection Section(ToolInspection inspection, string heading) =>
        inspection.Sections.Single(s => s.Heading == heading);

    static string Status(ToolInspection inspection) => Section(inspection, ToolInspection.StatusHeading).Text;

    /// The row keeps the description; opening it shows the command that actually ran.
    [Test]
    public async Task A_described_command_opens_on_the_command_it_ran_and_its_output() {
        var inspection = ToolInspection.Build("Bash",
            """{"command":"rm -rf node_modules && npm ci","description":"Reinstall dependencies"}""",
            "added 12 packages", ToolOutcome.Done, awaitingPermission: false);

        await Assert.That(inspection.Sections.Select(s => s.Heading))
            .IsEquivalentTo(new[] { "Status", "Command", "Output" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Status(inspection)).IsEqualTo("Finished");
        await Assert.That(Section(inspection, ToolInspection.CommandHeading).Text).IsEqualTo("rm -rf node_modules && npm ci");
        await Assert.That(Section(inspection, ToolInspection.OutputHeading).Text).IsEqualTo("added 12 packages");
        await Assert.That(inspection.Sections.Any(s => s.IsMissing || s.HasNote)).IsFalse();
    }

    /// Every vendor's command shape opens the same way: Claude's string, Codex's cmd and argv.
    [Test]
    [Arguments("Bash", """{"command":"dotnet test"}""")]
    [Arguments("exec_command", """{"cmd":"dotnet test","workdir":"/r"}""")]
    [Arguments("shell", """{"command":["bash","-lc","dotnet test"]}""")]
    [Arguments("local_shell", """{"command":["dotnet","test"]}""")]
    public async Task Each_vendor_command_shape_opens_on_the_same_command(string name, string input) {
        var inspection = ToolInspection.Build(name, input, "Passed!", ToolOutcome.Done, awaitingPermission: false);
        await Assert.That(Section(inspection, ToolInspection.CommandHeading).Text).IsEqualTo("dotnet test");
    }

    [Test]
    [Arguments(ToolOutcome.Error, false, "Failed")]
    [Arguments(ToolOutcome.Done, false, "Finished")]
    [Arguments(ToolOutcome.Running, false, "Running")]
    [Arguments(ToolOutcome.Running, true, "Waiting for permission")]
    public async Task Status_names_the_recorded_outcome(ToolOutcome outcome, bool awaiting, string expected) {
        var inspection = ToolInspection.Build("Bash", """{"command":"dotnet test"}""", "x", outcome, awaiting);
        await Assert.That(Status(inspection)).IsEqualTo(expected);
    }

    /// An absent result is said to be absent; nothing stands in for it.
    [Test]
    [Arguments(ToolOutcome.Running, null, ToolInspection.NoResult)]
    [Arguments(ToolOutcome.Done, null, ToolInspection.NoOutput)]
    [Arguments(ToolOutcome.Error, "", ToolInspection.NoOutput)]
    [Arguments(ToolOutcome.Done, "  \n", ToolInspection.NoOutput)]
    public async Task A_missing_result_reads_as_missing(ToolOutcome outcome, string? result, string expected) {
        var output = Section(ToolInspection.Build("Bash", """{"command":"true"}""", result, outcome, false), ToolInspection.OutputHeading);
        await Assert.That(output.IsMissing).IsTrue();
        await Assert.That(output.Text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("{}")]
    public async Task A_missing_input_reads_as_missing(string? input) {
        var section = ToolInspection.Build("Read", input, "x", ToolOutcome.Done, false).Sections[1];
        await Assert.That(section.Heading).IsEqualTo(ToolInspection.InputHeading);
        await Assert.That(section.IsMissing).IsTrue();
        await Assert.That(section.Text).IsEqualTo(ToolInspection.NoInput);
    }

    /// A result the transcript cut keeps what it carried and says it was cut.
    [Test]
    public async Task A_result_cut_at_the_transcript_cap_says_so() {
        var capped = TranscriptEnvelopes.Cap(new string('x', 5000));
        var output = Section(ToolInspection.Build("Bash", """{"command":"make"}""", capped, ToolOutcome.Done, false), ToolInspection.OutputHeading);
        await Assert.That(output.Text).IsEqualTo(capped);
        await Assert.That(output.Note).IsEqualTo("Cut to 4,096 characters when it was recorded");
    }

    [Test]
    public async Task A_result_under_the_cap_carries_no_cut_note() {
        var output = Section(ToolInspection.Build("Bash", """{"command":"make"}""", "done…", ToolOutcome.Done, false), ToolInspection.OutputHeading);
        await Assert.That(output.HasNote).IsFalse();
    }

    [Test]
    public async Task Redacted_text_is_marked_wherever_it_appears() {
        var inspection = ToolInspection.Build("Bash", """{"command":"curl -H 'Authorization: [REDACTED]' x"}""",
            "token=[REDACTED]", ToolOutcome.Done, false);
        await Assert.That(Section(inspection, ToolInspection.CommandHeading).Note).IsEqualTo("Secrets were redacted when it was recorded");
        await Assert.That(Section(inspection, ToolInspection.OutputHeading).Note).IsEqualTo("Secrets were redacted when it was recorded");
    }

    /// Live output is not capped by the transcript; the row shows a bounded part and says which.
    [Test]
    public async Task An_output_over_the_display_cap_shows_its_start_and_says_so() {
        var output = Section(ToolInspection.Build("exec_command", """{"cmd":"cat big"}""", new string('y', ToolInspection.DisplayCap + 10),
            ToolOutcome.Done, false), ToolInspection.OutputHeading);
        await Assert.That(output.Text.Length).IsEqualTo(ToolInspection.DisplayCap);
        await Assert.That(output.Note).IsEqualTo("Showing the first 16,000 characters");
    }

    [Test]
    public async Task A_non_command_call_lists_its_arguments_as_given() {
        var input = ToolInspection.Build("Edit", """{"file_path":"src/a.cs","old_string":"a\n  b","replace_all":false}""", "ok", ToolOutcome.Done, false)
            .Sections[1];
        await Assert.That(input.Heading).IsEqualTo(ToolInspection.InputHeading);
        await Assert.That(input.Text).IsEqualTo("file_path: src/a.cs\nold_string:\na\n  b\nreplace_all: false");
    }

    /// A Codex patch rides as `input` in its rollout and as `command` in its hook.
    [Test]
    [Arguments("input")]
    [Arguments("command")]
    public async Task A_patch_opens_on_the_patch_text(string key) {
        const string patch = "*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** End Patch";
        var json = $$"""{"{{key}}":"*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** End Patch"}""";
        var section = ToolInspection.Build("apply_patch", json, "Success", ToolOutcome.Done, false).Sections[1];
        await Assert.That(section.Heading).IsEqualTo(ToolInspection.PatchHeading);
        await Assert.That(section.Text).IsEqualTo(patch);
    }

    /// A command tool whose input names no command still shows what it was given.
    [Test]
    public async Task A_command_tool_without_a_command_lists_its_input() {
        var section = ToolInspection.Build("BashOutput", """{"bash_id":"b1"}""", "x", ToolOutcome.Done, false).Sections[1];
        await Assert.That(section.Heading).IsEqualTo(ToolInspection.InputHeading);
        await Assert.That(section.Text).IsEqualTo("bash_id: b1");
    }

    [Test]
    public async Task Input_that_is_not_an_object_shows_as_recorded() {
        var section = ToolInspection.Build("Bash", "not json", "x", ToolOutcome.Done, false).Sections[1];
        await Assert.That(section.Heading).IsEqualTo(ToolInspection.InputHeading);
        await Assert.That(section.Text).IsEqualTo("not json");
    }
}
