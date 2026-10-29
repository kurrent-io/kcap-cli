using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The three evidence resources are hashed by file name over their exact text; the preambles hold no placeholder
/// braces; the question template names the eight tools and none of the legacy ones; and the renderer never re-scans an
/// inserted value.</summary>
public class EvidencePreambleHashesTests {
    [Test]
    public async Task Each_resource_is_hashed_by_its_file_name() {
        var hashes = EvidencePreambleHashes.Compute();

        await Assert.That(hashes.Keys.Order(StringComparer.Ordinal).ToList()).IsEquivalentTo(
            new[] { "preamble-eval-oneshot.txt", "preamble-eval-retrospective.txt", "prompt-eval-question-evidence.txt" });
        foreach (var (name, hash) in hashes) {
            await Assert.That(hash).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddedResources.Load(name)))));
            await Assert.That(hash.Length).IsEqualTo(64);
        }
    }

    [Test]
    public async Task The_preambles_carry_no_placeholder_braces() {
        foreach (var name in new[] { EvidencePromptBlocks.OneShotPreambleResource, EvidencePromptBlocks.RetrospectivePreambleResource }) {
            var text = EmbeddedResources.Load(name);
            await Assert.That(text.Contains('{')).IsFalse();
            await Assert.That(EvidencePromptBlocks.Preamble(name).EndsWith("\n\n", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task The_question_template_names_the_eight_tools_and_its_placeholders() {
        var template = EmbeddedResources.Load(EvidencePromptBlocks.QuestionTemplateResource);

        foreach (var tool in new[] { "list_sources", "list_turns", "read_events", "read_body", "list_calls", "summarize_calls", "list_authorizations", "open_page" })
            await Assert.That(template.Contains($"`{tool}(")).IsTrue();
        foreach (var legacy in new[] { "get_session_summary", "search_session", "get_transcript", "submit_verdict", "`note(" })
            await Assert.That(template.Contains(legacy)).IsFalse();
        foreach (var placeholder in new[] { "{SESSION_ID}", "{EVAL_RUN_ID}", "{MAX_TOOL_CALLS}", "{TASKS}", "{ORIENTATION}", "{CATEGORY}", "{QUESTION_ID}", "{QUESTION_TEXT}" })
            await Assert.That(template.Contains(placeholder)).IsTrue();
    }

    [Test]
    public async Task The_renderer_substitutes_once_and_never_re_scans_an_inserted_value() {
        var rendered = EvidencePromptBlocks.Render("{A} {B} {C} {A}", new Dictionary<string, string> { ["{A}"] = "{B}", ["{B}"] = "x" });

        await Assert.That(rendered).IsEqualTo("{B} x {C} {B}");
    }

    [Test]
    public async Task The_tasks_line_is_the_servers_read_from_evidence_line() =>
        await Assert.That(EvidencePromptBlocks.TasksReadFromEvidence)
            .IsEqualTo("Tasks: not supplied separately. Any plan or task list the session declared is part of the evidence; read it there.");
}
