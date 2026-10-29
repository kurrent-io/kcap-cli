using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The run's reporting question is the marked completion question, else the first completion question in run order,
/// else none; a mark on a question under another strategy counts for nothing. The server's vectors hold.</summary>
public class EvalStrategiesMirrorTests {
    static EvalQuestionDto Q(string id, string? strategy, bool marked = false) =>
        new() { Category = "c", Id = id, Text = id, Prompt = id, Strategy = strategy, ReportsObligations = marked };

    [Test]
    public async Task The_marked_completion_question_reports_else_the_first_else_none() {
        await Assert.That(EvalStrategiesMirror.ReportingQuestion([Q("a", "completion"), Q("b", "completion", marked: true)])).IsEqualTo("b");
        await Assert.That(EvalStrategiesMirror.ReportingQuestion([Q("s", "safety"), Q("a", "completion"), Q("b", "completion")])).IsEqualTo("a");
        await Assert.That(EvalStrategiesMirror.ReportingQuestion([Q("s", "safety", marked: true), Q("g", null)])).IsNull();
    }

    [Test]
    public async Task Every_server_reporting_question_vector_holds() {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-strategies", "reporting-question.json")));

        foreach (var v in vectors.RootElement.EnumerateArray()) {
            var questions = v.GetProperty("questions").EnumerateArray()
                .Select(q => Q(q.GetProperty("key").GetString()!, q.GetProperty("strategy").GetString(), q.GetProperty("reports_obligations").GetBoolean())).ToList();
            await Assert.That(EvalStrategiesMirror.ReportingQuestion(questions)).IsEqualTo(v.GetProperty("expected").GetString());
        }
    }
}
