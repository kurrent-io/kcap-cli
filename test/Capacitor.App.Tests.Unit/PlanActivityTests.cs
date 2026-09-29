using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// The tracker as a pure fold over the lines of each transcript read: it reports a plan write
/// once its result has landed, and nothing else.
public class PlanActivityTests {
    const string UpdateTask = "mcp__plugin_kcap_kcap-plans__update_plan_task";

    static AcpEventEnvelope Call(string callId, string toolName) => new(Kind: AcpEventKind.ToolCall, ToolCallId: callId, ToolName: toolName);

    static AcpEventEnvelope Result(string callId, bool isError = false) => new(Kind: AcpEventKind.ToolResult, ToolCallId: callId, ToolIsError: isError);

    static ChatProjectionResult Line(params AcpEventEnvelope[] envelopes) => new(envelopes, [], []);

    static (PlanActivity Activity, Func<int> Writes) Tracked() {
        var activity = new PlanActivity();
        var writes = 0;
        activity.PlanWritten += () => writes++;
        return (activity, () => writes);
    }

    [Test]
    public async Task A_plan_write_is_reported_when_its_result_lands_not_when_it_is_called() {
        var (activity, writes) = Tracked();

        activity.Apply([Line(Call("c1", UpdateTask))]);
        await Assert.That(writes()).IsEqualTo(0);

        activity.Apply([Line(Result("c1"))]);
        await Assert.That(writes()).IsEqualTo(1);
    }

    [Test]
    public async Task A_result_is_reported_once_however_often_it_is_projected() {
        var (activity, writes) = Tracked();
        activity.Apply([Line(Call("c1", UpdateTask), Result("c1"))]);

        activity.Apply([Line(Result("c1"))]);

        await Assert.That(writes()).IsEqualTo(1);
    }

    [Test]
    public async Task A_replayed_history_of_many_writes_across_many_lines_is_one_report() {
        var (activity, writes) = Tracked();

        activity.Apply([
            Line(Call("c1", UpdateTask)), Line(Result("c1")),
            Line(Call("c2", "set_plan_tasks"), Result("c2")),
            Line(Call("c3", UpdateTask)), Line(Result("c3")),
        ]);

        await Assert.That(writes()).IsEqualTo(1);
    }

    [Test]
    public async Task Another_tools_result_and_a_plan_read_report_nothing() {
        var (activity, writes) = Tracked();

        activity.Apply([Line(Call("c1", "Bash"), Result("c1"), Call("c2", "mcp__plugin_kcap_kcap-plans__get_plan"), Result("c2"))]);

        await Assert.That(writes()).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_write_changed_nothing_and_reports_nothing() {
        var (activity, writes) = Tracked();

        activity.Apply([Line(Call("c1", UpdateTask), Result("c1", isError: true))]);

        await Assert.That(writes()).IsEqualTo(0);
    }

    /// The chat applies the tracker before it builds its own rows, so a subscriber that throws
    /// would cost the chat that batch for good.
    [Test]
    [NotInParallel]
    public async Task A_subscriber_that_throws_does_not_reach_the_caller() {
        var (activity, writes) = Tracked();
        activity.PlanWritten += () => throw new InvalidOperationException("section gone");
        using var stderr = ConsoleOutput.StartErrorCapture();

        activity.Apply([Line(Call("c1", UpdateTask), Result("c1"))]);

        await Assert.That(writes()).IsEqualTo(1);
        await Assert.That(stderr.GetCapturedError()).Contains("section gone");
    }

    [Test]
    public async Task Clearing_forgets_the_calls_in_flight() {
        var (activity, writes) = Tracked();
        activity.Apply([Line(Call("c1", UpdateTask))]);

        activity.Clear();
        activity.Apply([Line(Result("c1"))]);

        await Assert.That(writes()).IsEqualTo(0);
    }
}
