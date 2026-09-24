namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

using Capacitor.Models.Transcripts.Harness.Claude;

public class ClaudeShellStepsTests {
    [Test]
    public async Task A_bash_call_and_its_results_are_read() {
        var call = ClaudeShellSteps.Read(
            """{"type":"assistant","cwd":"/repo","timestamp":"2026-09-23T12:00:00Z","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"git commit -q"}},{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"a"}}]}}""");
        var results = ClaudeShellSteps.Read(
            """{"type":"user","cwd":"/repo","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]},{"type":"tool_result","tool_use_id":"t2","content":"x","is_error":true}]}}""");

        await Assert.That(call!.Cwd).IsEqualTo("/repo");
        await Assert.That(call.At).IsEqualTo(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(call.Calls).IsEquivalentTo([new ShellSteps.Invocation("t1", "git commit -q")]);
        await Assert.That(results!.Results).IsEquivalentTo([new ShellSteps.Result("t1", "a\nb", false), new ShellSteps.Result("t2", "x", true)]);
    }

    [Test]
    [Arguments("{\"message\":\"tool_use\"}")]
    [Arguments("[\"tool_use\"]")]
    [Arguments("{\"message\":{\"content\":[\"tool_use\"]}}")]
    [Arguments("{\"tool_use\"")]
    public async Task A_line_of_an_unexpected_shape_yields_no_steps(string line) {
        var steps = ClaudeShellSteps.Read(line);

        await Assert.That(steps is null || steps is { Calls.Count: 0, Results.Count: 0 }).IsTrue();
    }
}
