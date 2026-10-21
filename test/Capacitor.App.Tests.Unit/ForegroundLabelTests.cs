using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ForegroundLabelTests {
    static ToolCallItem Call(string name, string detail, string? label = null) =>
        new(name, detail, ToolCategory.Command, label);

    [Test]
    [Arguments(5, "Thinking… 5s")]
    [Arguments(65, "Thinking… 1m 05s")]
    [Arguments(130, "Thinking… 2m 10s")]
    [Arguments(3600, "Thinking… 1h 00m")]
    public async Task Thinking_names_the_elapsed_time_when_nothing_is_running(int seconds, string expected) {
        await Assert.That(ChatTabViewModel.ForegroundLabel([], TimeSpan.FromSeconds(seconds)))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task One_running_tool_names_its_detail() {
        await Assert.That(ChatTabViewModel.ForegroundLabel([Call("Bash", "Print five ticks", "Ran command")], TimeSpan.Zero))
            .IsEqualTo("Running: Print five ticks");
    }

    [Test]
    public async Task Several_running_tools_are_counted() {
        await Assert.That(ChatTabViewModel.ForegroundLabel([Call("Bash", "a"), Call("Bash", "b")], TimeSpan.Zero))
            .IsEqualTo("Running 2 tools…");
    }
}
