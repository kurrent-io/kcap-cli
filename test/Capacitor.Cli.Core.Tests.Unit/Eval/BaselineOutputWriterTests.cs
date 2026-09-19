using System.Text.Json;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class BaselineOutputWriterTests {
    static readonly EvalUsage TextUsage = new() {
        InputTokens      = 1000,
        OutputTokens     = 200,
        CacheReadTokens  = 0,
        CacheWriteTokens = 0,
        ReportedCostUsd  = 0.01
    };

    static readonly EvalUsage ToolsUsage = new() {
        InputTokens      = 3000,
        OutputTokens     = 600,
        CacheReadTokens  = 500,
        CacheWriteTokens = 100,
        ReportedCostUsd  = 0.08
    };

    static readonly EvalUsage RetroUsage = new() {
        InputTokens      = 2000,
        OutputTokens     = 400,
        CacheReadTokens  = 0,
        CacheWriteTokens = 0,
        ReportedCostUsd  = 0.03
    };

    [Test]
    public async Task Writes_documented_snake_case_keys_with_totals_equal_to_the_sum() {
        var textQuestion = new BaselineQuestionOutput {
            QuestionId = "destructive_commands",
            Route      = "text",
            Usage      = TextUsage,
            Calls      = 1,
            ElapsedMs  = 1500
        };
        var toolsQuestion = new BaselineQuestionOutput {
            QuestionId = "tests_written",
            Route      = "tools",
            Usage      = ToolsUsage,
            Calls      = 1,
            ElapsedMs  = 4200
        };
        var retrospective = new BaselineRetrospectiveOutput {
            Usage     = RetroUsage,
            Calls     = 1,
            ElapsedMs = 2100
        };
        var totals = EvalUsage.Sum([textQuestion.Usage, toolsQuestion.Usage, retrospective.Usage]);

        var output = new BaselineOutput {
            SessionId      = "sess-abc",
            EvalRunId      = "run-1",
            Model          = "sonnet",
            Chain          = true,
            Questions      = [textQuestion, toolsQuestion],
            Retrospective  = retrospective,
            Totals         = totals,
            TotalElapsedMs = 9000
        };

        using var tmp  = new TempDir();
        var       path = tmp.PathTo("baseline.json");

        BaselineOutputWriter.Write(path, output);

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var       root = doc.RootElement;

        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo("sess-abc");
        await Assert.That(root.GetProperty("eval_run_id").GetString()).IsEqualTo("run-1");
        await Assert.That(root.GetProperty("model").GetString()).IsEqualTo("sonnet");
        await Assert.That(root.GetProperty("chain").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("total_elapsed_ms").GetInt64()).IsEqualTo(9000L);

        var questions = root.GetProperty("questions");
        await Assert.That(questions.GetArrayLength()).IsEqualTo(2);

        var q0 = questions[0];
        await Assert.That(q0.GetProperty("question_id").GetString()).IsEqualTo("destructive_commands");
        await Assert.That(q0.GetProperty("route").GetString()).IsEqualTo("text");
        await Assert.That(q0.GetProperty("calls").GetInt32()).IsEqualTo(1);
        await Assert.That(q0.GetProperty("elapsed_ms").GetInt64()).IsEqualTo(1500L);
        await Assert.That(q0.GetProperty("usage").GetProperty("input_tokens").GetInt64()).IsEqualTo(1000L);
        await Assert.That(q0.GetProperty("usage").GetProperty("output_tokens").GetInt64()).IsEqualTo(200L);
        await Assert.That(q0.GetProperty("usage").GetProperty("reported_cost_usd").GetDouble()).IsEqualTo(0.01);

        var q1 = questions[1];
        await Assert.That(q1.GetProperty("question_id").GetString()).IsEqualTo("tests_written");
        await Assert.That(q1.GetProperty("route").GetString()).IsEqualTo("tools");
        await Assert.That(q1.GetProperty("usage").GetProperty("cache_read_tokens").GetInt64()).IsEqualTo(500L);
        await Assert.That(q1.GetProperty("usage").GetProperty("cache_write_tokens").GetInt64()).IsEqualTo(100L);

        var retro = root.GetProperty("retrospective");
        await Assert.That(retro.GetProperty("calls").GetInt32()).IsEqualTo(1);
        await Assert.That(retro.GetProperty("elapsed_ms").GetInt64()).IsEqualTo(2100L);
        await Assert.That(retro.GetProperty("usage").GetProperty("input_tokens").GetInt64()).IsEqualTo(2000L);

        var totalsJson = root.GetProperty("totals");
        await Assert.That(totalsJson.GetProperty("input_tokens").GetInt64()).IsEqualTo(1000L + 3000L + 2000L);
        await Assert.That(totalsJson.GetProperty("output_tokens").GetInt64()).IsEqualTo(200L + 600L + 400L);
        await Assert.That(totalsJson.GetProperty("cache_read_tokens").GetInt64()).IsEqualTo(500L);
        await Assert.That(totalsJson.GetProperty("cache_write_tokens").GetInt64()).IsEqualTo(100L);
        await Assert.That(totalsJson.GetProperty("reported_cost_usd").GetDouble()).IsEqualTo(0.01 + 0.08 + 0.03);
    }

    [Test]
    public async Task Omitted_retrospective_serializes_as_null() {
        var question = new BaselineQuestionOutput {
            QuestionId = "solo",
            Route      = "text",
            Usage      = TextUsage,
            Calls      = 1,
            ElapsedMs  = 500
        };
        var output = new BaselineOutput {
            SessionId      = "sess-solo",
            EvalRunId      = "run-2",
            Model          = "sonnet",
            Chain          = false,
            Questions      = [question],
            Retrospective  = null,
            Totals         = question.Usage,
            TotalElapsedMs = 500
        };

        using var tmp  = new TempDir();
        var       path = tmp.PathTo("baseline-solo.json");

        BaselineOutputWriter.Write(path, output);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        await Assert.That(doc.RootElement.GetProperty("retrospective").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
