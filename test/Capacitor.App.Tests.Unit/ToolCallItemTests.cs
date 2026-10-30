using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// Opening a row in isolation. Pure: Toggle is called directly, so no ReactiveCommand is built.
public class ToolCallItemTests {
    static ToolCallItem Shell() => new("Bash", "Reinstall dependencies", ToolCategory.Command, "Ran command",
        inputJson: """{"command":"rm -rf node_modules && npm ci","description":"Reinstall dependencies"}""");

    [Test]
    public async Task A_closed_row_builds_no_inspection() {
        var call = Shell();
        await Assert.That(call.IsExpanded).IsFalse();
        await Assert.That(call.Inspection).IsNull();
    }

    [Test]
    public async Task Opening_a_row_shows_what_its_call_recorded_and_closing_drops_it() {
        var call = Shell();
        call.Result = "added 12 packages";
        call.Outcome = ToolOutcome.Error;

        call.Toggle();
        await Assert.That(call.IsExpanded).IsTrue();
        var sections = call.Inspection!.Sections;
        await Assert.That(sections.Single(s => s.Heading == ToolInspection.StatusHeading).Text).IsEqualTo("Failed");
        await Assert.That(sections.Single(s => s.Heading == ToolInspection.CommandHeading).Text).IsEqualTo("rm -rf node_modules && npm ci");
        await Assert.That(sections.Single(s => s.Heading == ToolInspection.OutputHeading).Text).IsEqualTo("added 12 packages");

        call.Toggle();
        await Assert.That(call.Inspection).IsNull();
    }

    /// A row opened while the call runs follows it to its result.
    [Test]
    public async Task An_open_row_follows_its_call_to_the_result() {
        var call = Shell();
        call.Toggle();
        await Assert.That(call.Inspection!.Sections[^1].Text).IsEqualTo(ToolInspection.NoResult);
        var raised = new List<string?>();
        call.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        call.Result = "ok";
        call.Outcome = ToolOutcome.Done;

        await Assert.That(raised).Contains(nameof(ToolCallItem.Inspection));
        await Assert.That(call.Inspection!.Sections[^1].Text).IsEqualTo("ok");
        await Assert.That(call.Inspection.Sections[0].Text).IsEqualTo("Finished");
    }

    [Test]
    public async Task The_toggle_names_the_row_and_what_it_will_do() {
        var call = Shell();
        await Assert.That(call.ToggleAccessibleName).IsEqualTo("Show details: Ran command Reinstall dependencies");
        call.Toggle();
        await Assert.That(call.ToggleAccessibleName).IsEqualTo("Hide details: Ran command Reinstall dependencies");
    }
}
