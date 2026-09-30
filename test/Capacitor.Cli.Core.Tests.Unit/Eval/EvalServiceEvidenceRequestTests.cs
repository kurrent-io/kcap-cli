using System.Text.Json;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>The evidence route's requests: the verdict schema is the legacy one plus optional citations, the MCP config
/// hands the MCP process a run-file path and nothing secret, the allowlist is the eight tools, and both prompts are rendered
/// in one pass so session text is never re-scanned.</summary>
public class EvalServiceEvidenceRequestTests {
    static EvalQuestionDto Question(string prompt, string? raw = null) =>
        new() { Category = "safety", Id = "q1", Text = "t", Prompt = prompt, RawText = raw };

    [Test]
    public async Task The_evidence_verdict_schema_is_the_legacy_schema_plus_optional_citations() {
        using var legacy   = JsonDocument.Parse(EvalService.GetVerdictJsonSchema());
        using var evidence = JsonDocument.Parse(EvalService.EvidenceVerdictJsonSchema);
        var legacyProperties   = legacy.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        var evidenceProperties = evidence.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        await Assert.That(evidenceProperties).IsEquivalentTo([.. legacyProperties, "citations"]);
        await Assert.That(evidence.RootElement.GetProperty("required").GetRawText()).IsEqualTo(legacy.RootElement.GetProperty("required").GetRawText());
        await Assert.That(evidence.RootElement.GetProperty("properties").GetProperty("citations").GetProperty("maxItems").GetInt32()).IsEqualTo(EvidenceBudgets.MaxCitations);
    }

    [Test]
    public async Task The_mcp_config_passes_the_run_file_and_nothing_secret() {
        var config = EvalService.BuildEvidenceMcpConfig("/opt/kcap", "sess-1", "/tmp/kcap-eval-x/q1.run.json", "https://tenant.example");

        await Assert.That(config).IsEqualTo("""{"mcpServers":{"kcap-judge":{"command":"/opt/kcap","args":["mcp","judge","--session","sess-1","--run","/tmp/kcap-eval-x/q1.run.json"],"env":{"KCAP_URL":"https://tenant.example"}}}}""");
        await Assert.That(string.Join(",", EvalService.EvidenceMcpAllowedTools)).IsEqualTo(
            "mcp__kcap-judge__list_sources,mcp__kcap-judge__list_turns,mcp__kcap-judge__read_events,mcp__kcap-judge__read_body,"
          + "mcp__kcap-judge__list_calls,mcp__kcap-judge__summarize_calls,mcp__kcap-judge__list_authorizations,mcp__kcap-judge__open_page");
    }

    [Test]
    public async Task The_one_shot_prompt_is_the_preamble_and_the_catalog_prompt_with_tasks_filled_in_one_pass() {
        var prompt = EvalService.BuildOneShotPrompt(Question("{SESSION_ID}|{CATEGORY}|{QUESTION_ID}|{TASKS}|{TRACE_JSON}{CACHE_BOUNDARY}"), "sess-1", "run-1", """[{"text":"{QUESTION_ID}"}]""");

        await Assert.That(prompt).IsEqualTo(EvidencePromptBlocks.Preamble(EvidencePromptBlocks.OneShotPreambleResource)
            + $"sess-1|safety|q1|{EvidencePromptBlocks.TasksReadFromEvidence}|[{{\"text\":\"{{QUESTION_ID}}\"}}]");
    }

    [Test]
    public async Task The_retrieval_prompt_carries_the_orientation_budget_and_raw_question_text() {
        var prompt = EvalService.BuildEvidenceQuestionPrompt(Question("RENDERED", raw: "Was anything deleted?"), "sess-1", "run-1", "ORIENTATION {TASKS}", 48);

        await Assert.That(prompt.Contains("Was anything deleted?")).IsTrue();
        await Assert.That(prompt.Contains("RENDERED")).IsFalse();
        await Assert.That(prompt.Contains("at most 48 tool calls")).IsTrue();
        await Assert.That(prompt.Contains("ORIENTATION {TASKS}")).IsTrue();
        await Assert.That(prompt.Contains(EvidencePromptBlocks.TasksReadFromEvidence)).IsTrue();
        await Assert.That(prompt.Contains("{SESSION_ID}")).IsFalse();
    }

    [Test]
    public async Task Citation_tokens_are_read_from_the_reply_and_a_malformed_reply_has_none() {
        await Assert.That(EvalService.ParseCitationTokens("""{"finding":"f","citations":["p1.2","AgentSession-r@3",7]}""")).IsEquivalentTo(["p1.2", "AgentSession-r@3"]);
        await Assert.That(EvalService.ParseCitationTokens("not json")).IsEmpty();
        await Assert.That(EvalService.ParseCitationTokens("""["p1.1"]""")).IsEmpty();
    }
}
