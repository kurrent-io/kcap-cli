using System.Text.Json;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The CLI's mirrors bind the server's evidence wire names, and the new contract fields leave a legacy payload's
/// bytes untouched.</summary>
public class EvidenceWireMirrorTests {
    const string Catalog = """{"retrospective_prompt":"r","retrospective_prompt_version":"1","questions":[]""";

    [Test]
    public async Task The_catalog_binds_the_advertisement_and_reads_null_without_it() {
        var with = JsonSerializer.Deserialize(Catalog + ""","evidence_retrieval":{"max_tool_calls":48,"judge_byte_budget_bytes":600000,"page_budget_bytes":65536,"one_shot_limit_chars":400000,"retrospective_evidence_bytes":200000,"coverage_policy_version":"coverage-v2"}}""",
            CapacitorJsonContext.Default.EvalCatalogDto)!;
        var without = JsonSerializer.Deserialize(Catalog + "}", CapacitorJsonContext.Default.EvalCatalogDto)!;

        await Assert.That(with.EvidenceRetrieval).IsNotNull();
        await Assert.That(with.EvidenceRetrieval!.MaxToolCalls).IsEqualTo(48);
        await Assert.That(with.EvidenceRetrieval.JudgeByteBudgetBytes).IsEqualTo(600_000);
        await Assert.That(with.EvidenceRetrieval.PageBudgetBytes).IsEqualTo(65_536);
        await Assert.That(with.EvidenceRetrieval.OneShotLimitChars).IsEqualTo(400_000);
        await Assert.That(with.EvidenceRetrieval.RetrospectiveEvidenceBytes).IsEqualTo(200_000);
        await Assert.That(with.EvidenceRetrieval.CoveragePolicyVersion).IsEqualTo("coverage-v2");
        await Assert.That(without.EvidenceRetrieval).IsNull();
    }

    [Test]
    public async Task The_manifest_binds_issue_and_expiry_and_reads_null_without_them() {
        const string page = """{"scope_version":"v","root_session_id":"r","complete":true,"incomplete_reasons":[],"sources":[{"source_id":"AgentSession-r","kind":"root","session_id":"r","chain_index":0,"depth":0,"revision_cutoff":9,"first_revision":0,"turn_count":3,"availability":"available","discovered_by":"root"}],"next_cursor":null,"token":"t""";
        var with    = JsonSerializer.Deserialize(page + "\",\"issued_at\":\"2026-09-23T12:00:00+00:00\",\"expires_at\":\"2026-09-23T12:30:00+00:00\"}", CapacitorJsonContext.Default.EvidenceScopeManifestDto)!;
        var without = JsonSerializer.Deserialize(page + "\"}", CapacitorJsonContext.Default.EvidenceScopeManifestDto)!;

        await Assert.That(with.ExpiresAt!.Value - with.IssuedAt!.Value).IsEqualTo(TimeSpan.FromMinutes(30));
        await Assert.That(with.Sources.Single().TurnCount).IsEqualTo(3);
        await Assert.That(with.Sources.Single().IsAvailable).IsTrue();
        await Assert.That(without.IssuedAt).IsNull();
        await Assert.That(without.ExpiresAt).IsNull();
    }

    [Test]
    public async Task Certification_records_round_trip_in_snake_case() {
        var request = JsonSerializer.Serialize(new EvidenceCitationsRequestDto { Token = "t", Refs = ["s@1"] }, CapacitorJsonContext.Default.EvidenceCitationsRequestDto);
        await Assert.That(request).IsEqualTo("""{"token":"t","refs":["s@1"]}""");

        var response = JsonSerializer.Deserialize("""{"scope_version":"v","citations":[{"ref":"s@1","state":"certified","digest":"ab","code":null},{"ref":"s@2","state":"refused","digest":null,"code":"work_budget"}]}""",
            CapacitorJsonContext.Default.EvidenceCitationsResponseDto)!;
        await Assert.That(response.Citations.Select(c => (c.Ref, c.State, c.Code))).IsEquivalentTo([("s@1", "certified", (string?)null), ("s@2", "refused", "work_budget")]);
    }

    [Test]
    public async Task The_hub_records_carry_the_new_trailing_fields_under_their_wire_names() {
        var prepare = JsonSerializer.Serialize(new PrepareResult(true, null, "s", 1, 2, 0, 0, 0, "v", "evidence_retrieval", 3, null), CapacitorJsonContext.Default.PrepareResult);
        await Assert.That(prepare).Contains("\"evidence_scope_version\":\"v\"").And.Contains("\"route\":\"evidence_retrieval\"").And.Contains("\"source_count\":3");

        var result = JsonSerializer.Serialize(new QuestionResultV2(null, null, "moved", 0, 0, RunFailure: "scope_moved"), CapacitorJsonContext.Default.QuestionResultV2);
        await Assert.That(result).Contains("\"run_failure\":\"scope_moved\"");
    }

    [Test]
    public async Task A_legacy_shaped_hub_record_omits_the_new_trailing_keys() {
        var prepare = JsonSerializer.Serialize(new PrepareResult(true, null, "s", 1, 2, 0, 0, 0), CapacitorJsonContext.Default.PrepareResult);
        await Assert.That(prepare).IsEqualTo(
            """{"success":true,"error":null,"canonical_session_id":"s","trace_entries":1,"trace_chars":2,"tool_results_total":0,"tool_results_truncated":0,"bytes_saved":0}""");

        var result = JsonSerializer.Serialize(new QuestionResultV2(null, null, "moved", 0, 0), CapacitorJsonContext.Default.QuestionResultV2);
        await Assert.That(result).IsEqualTo(
            """{"assessment":null,"failure":null,"error":"moved","input_tokens":0,"output_tokens":0}""");
    }

    [Test]
    public async Task An_assessment_without_the_new_fields_serializes_as_it_always_has() {
        var a = new EvalQuestionAssessment { Category = "c", QuestionId = "q", Outcome = "assessed", Score = 5, Verdict = "pass", Finding = "f" };
        var json = JsonSerializer.Serialize(a, CapacitorJsonContext.Default.EvalQuestionAssessment);

        await Assert.That(json).IsEqualTo("""{"category":"c","question_id":"q","outcome":"assessed","score":5,"verdict":"pass","finding":"f","evidence":null,"recommendation":null,"tools_used":null,"prompt_version":null}""");
    }

    [Test]
    public async Task A_ref_parses_to_its_canonical_form_and_a_source_decodes_to_a_session_stream() {
        await Assert.That(EvidenceRefText.TryParse("AgentSession-abc@4", out var e)).IsTrue();
        await Assert.That((e.Form, e.A)).IsEqualTo((EvidenceRefForm.Event, 4L));
        await Assert.That(EvidenceRefText.TryParse("AgentSession-abc@4-9", out var r)).IsTrue();
        await Assert.That(r.ToString()).IsEqualTo("AgentSession-abc@4-9");
        await Assert.That(EvidenceRefText.TryParse("AgentSubsession-abc-agent%2Da#g2t7", out var t)).IsTrue();
        await Assert.That((t.Form, t.A, t.B)).IsEqualTo((EvidenceRefForm.Turn, 2L, 7L));
        foreach (var bad in new[] { "", "@4", "AgentSession-abc@04", "AgentSession-abc@9-4", "Other-abc@1", "AgentSession-abc#t1" })
            await Assert.That(EvidenceRefText.TryParse(bad, out _)).IsFalse();
        await Assert.That(EvidenceRefText.TryDecodeSource("AgentSubsession-abc-agent%2Da", out var stream)).IsTrue();
        await Assert.That(stream).IsEqualTo("AgentSubsession-abc-agent-a");
    }

    const string Lane = "PlanLane-0000000000000000000000000000f001-00000000000000000000000000000001";

    [Test]
    public async Task A_plan_lane_ref_parses_as_an_event_or_range_and_never_as_a_turn() {
        await Assert.That(EvidenceRefText.TryParse($"{Lane}@3", out var e)).IsTrue();
        await Assert.That((e.SourceId, e.Form, e.A)).IsEqualTo((Lane, EvidenceRefForm.Event, 3L));
        await Assert.That(EvidenceRefText.TryParse($"{Lane}@1-4", out var r)).IsTrue();
        await Assert.That((r.Form, r.A, r.B)).IsEqualTo((EvidenceRefForm.Range, 1L, 4L));
        await Assert.That(EvidenceRefText.TryDecodeSource(Lane, out _)).IsTrue();

        foreach (var bad in new[] { $"{Lane}#g1t2", "PlanLane-0000000000000000000000000000F001-s@1", "PlanLane-f001-s@1", "PlanLane-0000000000000000000000000000f001-@1", "PlanLane-0000000000000000000000000000f001@1" })
            await Assert.That(EvidenceRefText.TryParse(bad, out _)).IsFalse();
    }

    [Test]
    public async Task A_plan_source_and_a_plan_entry_bind_their_plan_fields_and_read_null_without_them() {
        const string source = """{"source_id":"PlanLane-0000000000000000000000000000f001-00000000000000000000000000000001","kind":"plan","session_id":"00000000000000000000000000000001","revision_cutoff":3,"first_revision":0,"availability":"available","discovered_by":"plan_pointer","plan_id":"0000000000000000000000000000f001"}""";
        await Assert.That(JsonSerializer.Deserialize(source, CapacitorJsonContext.Default.EvidenceSourceDto)!.PlanId).IsEqualTo("0000000000000000000000000000f001");
        await Assert.That(JsonSerializer.Deserialize("""{"source_id":"AgentSession-r","kind":"root","session_id":"r","availability":"available"}""", CapacitorJsonContext.Default.EvidenceSourceDto)!.PlanId).IsNull();

        const string descriptor = """{"field":"payload","ordinal":null,"bytes":9,"ref":"x@1"}""";
        var entry = JsonSerializer.Deserialize($$$"""{"ref":"x@1","revision":1,"event_type":"PlanTasksDeclared","kind":"plan_entry","payload_body":{{{descriptor}}},"plan_kind":"tasks","plan_content":{"tasks":[{"id":"t1"}]}}""",
            CapacitorJsonContext.Default.EvidenceEventEntryDto)!;
        await Assert.That(entry.PlanKind).IsEqualTo("tasks");
        await Assert.That(entry.PlanContent!.Value.GetRawText()).IsEqualTo("""{"tasks":[{"id":"t1"}]}""");

        var plain = JsonSerializer.Deserialize($$"""{"ref":"x@1","revision":1,"event_type":"E","kind":"other","payload_body":{{descriptor}}}""", CapacitorJsonContext.Default.EvidenceEventEntryDto)!;
        await Assert.That(plain.PlanKind).IsNull();
        await Assert.That(plain.PlanContent).IsNull();
    }
}
