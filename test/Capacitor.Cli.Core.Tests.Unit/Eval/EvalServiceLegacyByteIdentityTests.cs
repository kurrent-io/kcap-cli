using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>The legacy requests are pinned to the bytes they had before the evidence route existed: the text prompt (which
/// never substitutes {TASKS}), the tools prompt, the judge MCP config, the six-tool allowlist, the retrospective prompt and
/// the verdict schema.</summary>
public class EvalServiceLegacyByteIdentityTests {
    static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static EvalQuestionDto Question(string prompt) => new() { Category = "safety", Id = "destructive_commands", Text = "t", Prompt = prompt };

    [Test]
    public async Task The_text_prompt_substitutes_what_it_always_did_and_leaves_tasks_alone() =>
        await Assert.That(EvalService.BuildTextQuestionPrompt(Question("Q {SESSION_ID} {EVAL_RUN_ID} {CATEGORY} {QUESTION_ID}{CACHE_BOUNDARY} {TASKS}\n{TRACE_JSON}"), "sess-1", "run-1", "[TRACE]"))
            .IsEqualTo("Q sess-1 run-1 safety destructive_commands {TASKS}\n[TRACE]");

    [Test]
    public async Task The_tools_prompt_is_byte_identical() {
        var template = EmbeddedResources.Load("prompt-eval-question-tools.txt");
        var prompt   = EvalService.BuildToolsQuestionPrompt(template, "sess-1", "run-1", Question("RAW text"), knownPatterns: "");
        await Assert.That(Sha(prompt)).IsEqualTo("01dab68b6bcc3404fc054e5b4e1516814bb4f55a4b07ba21adbcfbed3146b617");
    }

    [Test]
    public async Task The_judge_mcp_config_and_allowlist_are_byte_identical() {
        await Assert.That(EvalService.BuildJudgeMcpConfig("/opt/kcap", "sess-1", "https://tenant.example"))
            .IsEqualTo("""{"mcpServers":{"kcap-judge":{"command":"/opt/kcap","args":["mcp","judge","--session","sess-1"],"env":{"KCAP_URL":"https://tenant.example"}}}}""");
        // Sent as one comma-joined --allowedTools value, so the order is part of the bytes.
        await Assert.That(string.Join(",", EvalService.JudgeMcpAllowedTools)).IsEqualTo(
            "mcp__kcap-judge__get_session_recap,mcp__kcap-judge__get_session_errors,mcp__kcap-judge__get_transcript,"
          + "mcp__kcap-judge__get_session_summary,mcp__kcap-judge__search_session,mcp__kcap-judge__get_tool_result");
    }

    [Test]
    public async Task The_retrospective_prompt_and_verdict_schema_are_byte_identical() {
        await Assert.That(EvalService.BuildRetrospectivePrompt("R {SESSION_META}|{VERDICTS_JSON}|{KNOWN_PATTERNS}|{TRACE_JSON}", "m", "[]", "", "T")).IsEqualTo("R m|[]||T");
        await Assert.That(Sha(EvalService.GetVerdictJsonSchema())).IsEqualTo("888ca9bbcc235038afa8262b314f05808e9a633025cfbc1b7edaf3b7cd7396e1");
    }
}
