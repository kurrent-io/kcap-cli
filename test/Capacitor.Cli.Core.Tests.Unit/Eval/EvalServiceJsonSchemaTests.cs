using System.Text.Json;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>
/// Validates that <see cref="EvalService.GetRetrospectiveJsonSchema()"/> encodes
/// the structured <c>{text, audience}</c> suggestion shape so that the
/// Claude CLI judge is actually constrained to emit objects — not bare
/// strings — for every suggestion item. Regression guard for that shape.
/// </summary>
public class EvalServiceJsonSchemaTests {
    // EvalService is internal; Capacitor.Cli.Core ships InternalsVisibleTo
    // for Capacitor.Cli.Tests.Unit so the const is directly accessible.

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_is_object_not_string() {
        using var doc  = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root       = doc.RootElement;

        var itemsType = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("type")
            .GetString();

        await Assert.That(itemsType).IsEqualTo("object");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_has_text_and_audience_properties() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var itemProps = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("properties");

        await Assert.That(itemProps.TryGetProperty("text",     out _)).IsTrue();
        await Assert.That(itemProps.TryGetProperty("audience", out _)).IsTrue();
    }

    [Test]
    public async Task RetrospectiveJsonSchema_audience_enum_contains_agent_and_human() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var audienceEnum = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("audience")
            .GetProperty("enum");

        var values = audienceEnum.EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(values).Contains("agent");
        await Assert.That(values).Contains("human");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_required_includes_text_and_audience() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var required = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("required");

        var fields = required.EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(fields).Contains("text");
        await Assert.That(fields).Contains("audience");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_outer_required_includes_all_top_level_fields() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var required = root
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(required).Contains("overall");
        await Assert.That(required).Contains("strengths");
        await Assert.That(required).Contains("issues");
        await Assert.That(required).Contains("suggestions");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_maxItems_is_five() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var maxItems = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("maxItems")
            .GetInt32();

        await Assert.That(maxItems).IsEqualTo(5);
    }

    // ── VerdictJsonSchema (D13) ──────────────────────────────────────────────

    static JsonElement VerdictSchemaRoot() =>
        JsonDocument.Parse(EvalService.GetVerdictJsonSchema()).RootElement.Clone();

    [Test]
    public async Task VerdictJsonSchema_outcome_is_a_required_three_value_enum() {
        var root = VerdictSchemaRoot();

        var values = root.GetProperty("properties").GetProperty("outcome").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        await Assert.That(values).IsEquivalentTo(["assessed", "insufficient_evidence", "not_applicable"]);

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        await Assert.That(required).Contains("outcome");
    }

    [Test]
    public async Task VerdictJsonSchema_score_is_nullable_integer_with_bounds() {
        var score = VerdictSchemaRoot().GetProperty("properties").GetProperty("score");

        var types = score.GetProperty("type").EnumerateArray().Select(e => e.GetString()!).ToList();
        await Assert.That(types).IsEquivalentTo(["integer", "null"]);
        await Assert.That(score.GetProperty("minimum").GetInt32()).IsEqualTo(1);
        await Assert.That(score.GetProperty("maximum").GetInt32()).IsEqualTo(5);
    }

    [Test]
    public async Task VerdictJsonSchema_verdict_is_nullable_string() {
        var verdict = VerdictSchemaRoot().GetProperty("properties").GetProperty("verdict");

        var types = verdict.GetProperty("type").EnumerateArray().Select(e => e.GetString()!).ToList();
        await Assert.That(types).IsEquivalentTo(["string", "null"]);
    }

    [Test]
    public async Task VerdictJsonSchema_finding_has_min_length_one() {
        var finding = VerdictSchemaRoot().GetProperty("properties").GetProperty("finding");

        await Assert.That(finding.GetProperty("minLength").GetInt32()).IsEqualTo(1);
    }
}
