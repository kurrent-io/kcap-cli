using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Tests.Integration;

public class EvalToolsRoutingTests {
    // VERIFIED against EvalQuestionMetadata.All (NeedsTools == true) on the rebased main:
    // the FOUR tool-routed questions are exactly these (category → id):
    //   safety/destructive_commands, quality/tests_written, quality/broken_tests,
    //   efficiency/direct_approach.
    static readonly (string Category, string Id)[] ToolsQuestions = [
        ("safety",     "destructive_commands"),
        ("quality",    "tests_written"),
        ("quality",    "broken_tests"),
        ("efficiency", "direct_approach")
    ];

    // TEXT-path question (NeedsTools == false) — negative case
    static readonly EvalCatalogQuestionDto TextQuestion = new() {
        Category = "quality", Id = "well_scoped_tasks", Title = "t",
        QuestionText = "RAW well_scoped_tasks", Prompt = "RENDERED well_scoped_tasks",
        PromptVersion = "1", NeedsTools = false
    };

    [Test]
    public async Task Each_known_tools_question_reconciles_with_NeedsTools_and_RawText() {
        var catalog = new EvalCatalogDto {
            RetrospectivePrompt = "r {TRACE_JSON}", RetrospectivePromptVersion = "1",
            Questions = [
                .. ToolsQuestions.Select(q => new EvalCatalogQuestionDto {
                    Category = q.Category, Id = q.Id, Title = "t",
                    QuestionText = $"RAW {q.Id}", Prompt = $"RENDERED {q.Id}",
                    PromptVersion = "1", NeedsTools = true
                }),
                TextQuestion
            ]
        };

        string[] allIds = [.. ToolsQuestions.Select(q => q.Id), TextQuestion.Id];
        var reconciled = EvalService.ReconcileQuestions(allIds, catalog);

        foreach (var (cat, id) in ToolsQuestions) {
            var rq = reconciled.Single(r => r.Id == id);
            await Assert.That(rq.NeedsTools).IsTrue();
            await Assert.That(rq.RawText).IsEqualTo($"RAW {id}");

            // Tools path substitutes the RAW text into the embedded {QUESTION_TEXT} (option (i)
            // from Task 3 Step 4 — pass `q with { Prompt = q.RawText }` to BuildToolsQuestionPrompt).
            var toolsPrompt = EvalService.BuildToolsQuestionPrompt(
                "TOOLS WRAPPER: {QUESTION_TEXT} cat={CATEGORY}", "sess-1", "run-1",
                rq with { Prompt = rq.RawText! }, knownPatterns: "");
            await Assert.That(toolsPrompt).Contains($"RAW {id}");
            await Assert.That(toolsPrompt).Contains($"cat={cat}");
        }

        // Negative case: TEXT-path question must reconcile with NeedsTools == false
        await Assert.That(reconciled.Single(r => r.Id == "well_scoped_tasks").NeedsTools).IsFalse();
    }
}
