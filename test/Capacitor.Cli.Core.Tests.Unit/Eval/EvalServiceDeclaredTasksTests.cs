using System.Text.Json;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class EvalServiceDeclaredTasksTests {
    const string Block = "Declared tasks: 2/4 done\n1. [completed] Scaffold {TRACE_JSON} the module (source: mcp)";

    static EvalQuestionDto Question(string prompt) => new() { Category = "plan_adherence", Id = "completed_items", Text = "t", Prompt = prompt };

    [Test]
    public async Task The_text_prompt_fills_tasks_in_one_pass() {
        var prompt = EvalService.BuildTextQuestionPrompt(Question("Q {SESSION_ID}{CACHE_BOUNDARY} {TASKS}\n{TRACE_JSON}"), "sess-1", "run-1", "[{\"text\":\"{TASKS}\"}]", Block);

        await Assert.That(prompt).IsEqualTo($"Q sess-1 {Block}\n[{{\"text\":\"{{TASKS}}\"}}]");
    }

    [Test]
    public async Task The_tools_prompt_puts_the_block_in_a_section_before_the_question() {
        var template = EmbeddedResources.Load("prompt-eval-question-tools.txt");
        var with     = EvalService.BuildToolsQuestionPrompt(template, "sess-1", "run-1", Question("Were the tasks done?"), knownPatterns: "", tasks: Block);
        var without  = EvalService.BuildToolsQuestionPrompt(template, "sess-1", "run-1", Question("Were the tasks done?"), knownPatterns: "");

        await Assert.That(with).Contains(EvalService.DeclaredTasksSection + Block + "\n\n## Question\n");
        await Assert.That(with.Replace(EvalService.DeclaredTasksSection + Block + "\n\n", "")).IsEqualTo(without);
        await Assert.That(without).DoesNotContain("{DECLARED_TASKS}").And.DoesNotContain("Declared tasks");
    }

    [Test]
    public async Task The_block_is_read_from_the_context_and_absent_from_an_older_server() {
        const string Older = """{"session_id":"s","session_chain":["s"],"trace":[],"compaction":{"threshold_bytes":1,"entries":0,"tool_results_total":0,"tool_results_truncated":0,"bytes_saved":0}}""";
        var newer = Older[..^1] + ",\"tasks\":\"Declared tasks: 0/1 done\"}";

        await Assert.That(JsonSerializer.Deserialize(Older, CapacitorJsonContext.Default.EvalContextResult)!.Tasks).IsNull();
        await Assert.That(JsonSerializer.Deserialize(newer, CapacitorJsonContext.Default.EvalContextResult)!.Tasks).IsEqualTo("Declared tasks: 0/1 done");
    }
}
