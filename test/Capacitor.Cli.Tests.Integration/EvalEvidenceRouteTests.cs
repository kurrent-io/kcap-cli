using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>`kcap eval` against an advertising server and a scripted claude: the evidence route never fetches the whole trace,
/// chooses its arm by the fit test, persists certified coverage-v2, turns every harness stop into its coded result, ends the
/// run with nothing persisted when the scope moves, leaves a retained ledger per question for baselines, and removes its run
/// directory on every exit; the legacy path is unchanged by the advertisement.</summary>
[NotInParallel]
public class EvalEvidenceRouteTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();
    readonly TempDir _tmp = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); _tmp.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;
    static string Sid  => EvidenceServerStub.SessionId;
    static readonly string Digest = new('a', 64);

    const string Ad = """{"max_tool_calls":48,"judge_byte_budget_bytes":600000,"page_budget_bytes":65536,"one_shot_limit_chars":400000,"retrospective_evidence_bytes":200000,"coverage_policy_version":"coverage-v2"}""";

    const string CatalogQuestions = """
        [{"category":"safety","id":"q1","title":"t","question_text":"Was anything destroyed?","prompt":"ONE-SHOT {SESSION_ID} {CATEGORY} {QUESTION_ID} {TASKS}\n{TRACE_JSON}","prompt_version":"3","needs_tools":false},
         {"category":"safety","id":"q-special","title":"t","question_text":"Special?","prompt":"ONE-SHOT {QUESTION_ID}\n{TRACE_JSON}","prompt_version":"3","needs_tools":false},
         {"category":"safety","id":"q-tools","title":"t","question_text":"Tools?","prompt":"TOOLS {QUESTION_ID}","prompt_version":"3","needs_tools":true}]
        """;

    const string Retro = """{"overall":"fine","strengths":[],"issues":[],"suggestions":[]}""";

    const string LegacyContext = """
        {"session_id":"0123456789abcdef0123456789abcdef","session_chain":["0123456789abcdef0123456789abcdef"],"trace":[{"kind":"user","timestamp":"2026-09-23T12:00:00Z","text":"hi"}],"compaction":{"threshold_bytes":2000,"entries":1,"tool_results_total":0,"tool_results_truncated":0,"bytes_saved":0,"plan_discovery_degraded":false,"skipped_streams":0,"plan_artifacts_truncated":0,"plan_artifacts_unavailable":0,"plan_artifacts_dropped":0}}
        """;

    static string Envelope(string structured) =>
        $$"""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"structured_output":{{structured}},"usage":{"input_tokens":10,"output_tokens":5} }""";

    static string Verdict(string id, params string[] citations) =>
        $$"""{"category":"safety","question_id":"{{id}}","outcome":"assessed","score":4,"verdict":"pass","finding":"ok","evidence":null,"recommendation":null,"retain_fact":null,"citations":[{{string.Join(",", citations.Select(c => "\"" + c + "\""))}}]}""";

    string Dir(string name) => _tmp.PathTo(name);

    static FakeClaudeOnPath Claude(string dir, string verdict, string? special = null, string before = "") {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "verdict.json"), Envelope(verdict));
        File.WriteAllText(Path.Combine(dir, "retro.json"), Envelope(Retro));
        if (special is not null) File.WriteAllText(Path.Combine(dir, "special.json"), special);
        return new FakeClaudeOnPath($"""
            #!/bin/sh
            d='{dir}'
            n=$(ls "$d" | grep -c '^prompt-')
            printf '%s\n' "$@" > "$d/args-$n.txt"
            cat > "$d/prompt-$n.txt"
            {before}
            if grep -q '^Retro ' "$d/prompt-$n.txt"; then cat "$d/retro.json"
            elif [ -f "$d/special.json" ] && grep -q 'q-special' "$d/prompt-$n.txt"; then cat "$d/special.json"
            else cat "$d/verdict.json"; fi
            """);
    }

    // The gate-off V4 body for the q1 + q-tools legacy run, so a change landing on every legacy run alike is still caught.
    const string LegacyV4BodySha = "5dedc141ffc16920271b414b495efc1126d7abe24f8aaa1f2390faa3d21964a4";

    static string Sha(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    static string Prompt(string dir, int n) => File.ReadAllText(Path.Combine(dir, $"prompt-{n}.txt"));
    static string[] ArgsOf(string dir, int n) => File.ReadAllLines(Path.Combine(dir, $"args-{n}.txt"));
    static string After(string[] args, string flag) => args[Array.IndexOf(args, flag) + 1];

    void ServeCatalog(bool advertised = true) => _stub.Catalog(advertised ? Ad : null, "[]", CatalogQuestions);

    void ServeScope(long cutoff, int turns = 1, TimeSpan? lifetime = null) {
        var issued   = DateTimeOffset.UtcNow;
        var manifest = EvidenceServerStub.Manifest("v1", "tok", null, issued, issued + (lifetime ?? TimeSpan.FromMinutes(30)), [EvidenceServerStub.Source(Root, 0, cutoff, turns)]);
        _stub.FreshScope(manifest);
        _stub.CursorPage("tok", manifest);
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, cutoff)]));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());
        _stub.Route("POST", "evals/v4", 200, "{}");
        _stub.Route("POST", "judge-facts", 200, "{}");
    }

    void ServeEvents(params string[] texts) =>
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, texts.Select((t, i) => EvidenceServerStub.EventEntry(Root, i, t))));

    void Certify(string reference) =>
        _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{"ref":"{{reference}}","state":"certified","digest":"{{Digest}}"}]}""");

    static IReadOnlyList<EvalQuestionDto> Questions(params string[] ids) => [.. ids.Select(id => new EvalQuestionDto { Category = "safety", Id = id, Text = id, Prompt = id })];

    Task<SessionEvalCompletedPayloadV4?> Run(FakeClaudeOnPath claude, string[] ids, IEvalObserver observer, bool chain = false, int? threshold = null, TimeProvider? time = null, CancellationToken ct = default) =>
        EvalService.RunAsync(_stub.Url, _http, null, TestHarnesses.Under(Home, BinaryProbe.Searching(claude.BinDirectory)), Sid, "sonnet", chain, threshold, observer,
            time ?? TimeProvider.System, ct, "run-fixed", Questions(ids));

    Task<EvidenceRunSetup?> Prepare(FakeClaudeOnPath claude, IEvalObserver observer, TimeProvider time, string tempRoot, params string[] ids) {
        Directory.CreateDirectory(tempRoot);
        var catalog = JsonSerializer.Deserialize(
            $$"""{"retrospective_prompt":"Retro {SESSION_META} {VERDICTS_JSON} {TRACE_JSON}","retrospective_prompt_version":"7","questions":{{CatalogQuestions}},"evidence_retrieval":{{Ad}} }""",
            CapacitorJsonContext.Default.EvalCatalogDto)!;
        return EvalService.PrepareEvidenceAsync(_stub.Url, _http, null, TestHarnesses.Under(Home, BinaryProbe.Searching(claude.BinDirectory)), Sid, Questions(ids), catalog, "sonnet",
            observer, time, CancellationToken.None, "run-fixed", tempRoot);
    }

    // The fit test and the orientation page by budget_bytes; the retrospective's excerpts of cited refs do not.
    List<WireMock.Logging.ILogEntry> PreparationReads(string relative) =>
        [.. _stub.Requests(relative).Where(e => e.RequestMessage.Query?.ContainsKey("budget_bytes") == true)];

    JsonElement Payload() => JsonDocument.Parse(_stub.Requests("evals/v4").Single().RequestMessage.Body!).RootElement.Clone();

    (string Root, EnvScope Scope) TempRoot() {
        var root = Dir("tmp-root");
        Directory.CreateDirectory(root);
        return (root, EnvScope.Exclusive("TMPDIR", root + Path.DirectorySeparatorChar));
    }

    static bool NoRunDirectory(string root) => Directory.GetDirectories(root, EvidenceRunContext.DirectoryPrefix + "*").Length == 0;

    [Test]
    public async Task A_fitting_single_run_never_fetches_eval_context_and_persists_certified_coverage_v2() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var (root, tmp) = TempRoot();
        using var tmpScope = tmp;
        ServeCatalog(); ServeScope(cutoff: 1); ServeEvents("hello", "world"); Certify($"{Root}@0");
        var dir = Dir("c1");
        using var claude = Claude(dir, Verdict("q1", "e0"));
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["q1"], observer);

        await Assert.That(result).IsNotNull();
        await Assert.That(_stub.Requests("eval-context")).IsEmpty();
        var payload = Payload();
        await Assert.That(payload.GetProperty("coverage_policy_version").GetString()).IsEqualTo("coverage-v2");
        await Assert.That(payload.GetProperty("evidence_scope_version").GetString()).IsEqualTo("v1");
        var q = payload.GetProperty("categories")[0].GetProperty("questions")[0];
        await Assert.That(q.GetProperty("evidence_coverage").GetProperty("citations")[0].GetProperty("ref").GetString()).IsEqualTo($"{Root}@0");
        await Assert.That(q.GetProperty("evidence_coverage").GetProperty("citations")[0].GetProperty("digest").GetString()).IsEqualTo(Digest);
        await Assert.That(q.GetProperty("trace_coverage").GetProperty("mode").GetString()).IsEqualTo("one_shot");
        await Assert.That(observer.Completed.Single().Route).IsEqualTo("evidence_one_shot");
        await Assert.That(observer.Treatment!.GateOn).IsTrue();
        var prompt = Prompt(dir, 0);
        await Assert.That(prompt.StartsWith(EvidencePromptBlocks.Preamble(EvidencePromptBlocks.OneShotPreambleResource), StringComparison.Ordinal)).IsTrue();
        await Assert.That(prompt.Contains("hello")).IsTrue();
        await Assert.That(prompt.Contains(EvidencePromptBlocks.TasksReadFromEvidence)).IsTrue();
        await Assert.That(ArgsOf(dir, 0).Contains("--mcp-config")).IsFalse();
        await Assert.That(NoRunDirectory(root)).IsTrue();
    }

    [Test]
    public async Task A_session_ten_times_the_bound_takes_retrieval_with_one_outline_and_one_summary_read() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 124_999); Certify($"{Root}#g1t0");
        var dir = Dir("c2");
        using var claude = Claude(dir, Verdict("q1", "o1.1"));
        var observer = new RecordingEvalObserver();

        await Run(claude, ["q1"], observer);

        await Assert.That(PreparationReads("evidence-events")).IsEmpty();
        await Assert.That(PreparationReads("evidence-turns").Count).IsEqualTo(1);
        await Assert.That(_stub.Requests("evidence-calls/summary").Count).IsEqualTo(1);
        var args = ArgsOf(dir, 0);
        await Assert.That(After(args, "--max-turns")).IsEqualTo("52");
        await Assert.That(After(args, "--max-budget-usd")).IsEqualTo("1");
        await Assert.That(After(args, "--mcp-config").Contains("\"--run\"")).IsTrue();
        await Assert.That(After(args, "--mcp-config").Contains("tok")).IsFalse();
        await Assert.That(After(args, "--allowedTools")).IsEqualTo(string.Join(",", EvalService.EvidenceMcpAllowedTools));
        await Assert.That(Prompt(dir, 0).Contains(EvidenceOrientationBuilder.ScopeSentence)).IsTrue();
        var retrospective = Prompt(dir, 1);
        await Assert.That(retrospective.StartsWith(EvidencePromptBlocks.Preamble(EvidencePromptBlocks.RetrospectivePreambleResource), StringComparison.Ordinal)).IsTrue();
        await Assert.That(retrospective.Contains("Cited evidence (1):")).IsTrue();
        await Assert.That(ArgsOf(dir, 1).Contains("--mcp-config")).IsFalse();
        var q = Payload().GetProperty("categories")[0].GetProperty("questions")[0];
        await Assert.That(q.GetProperty("trace_coverage").GetProperty("mode").GetString()).IsEqualTo("evidence_retrieval");
        await Assert.That(q.GetProperty("evidence_coverage").GetProperty("citations")[0].GetProperty("ref").GetString()).IsEqualTo($"{Root}#g1t0");
        await Assert.That(observer.Completed.Single().Route).IsEqualTo("evidence_retrieval");
    }

    [Test]
    public async Task No_read_names_a_source_outside_the_manifest() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 124_999); Certify($"{Root}#g1t0");
        using var claude = Claude(Dir("c3"), Verdict("q1", "o1.1"));

        await Run(claude, ["q1"], new RecordingEvalObserver());

        foreach (var entry in _stub.Server.LogEntries.Where(e => e.RequestMessage.Path.Contains("/evidence-", StringComparison.Ordinal))) {
            var query = entry.RequestMessage.Query ?? new Dictionary<string, WireMock.Types.WireMockList<string>>();
            foreach (var value in query.Where(kv => kv.Key is "source" or "ref").SelectMany(kv => kv.Value))
                await Assert.That(value.StartsWith(Root, StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task An_error_max_turns_envelope_persists_iteration_cap_with_its_detail() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 124_999, turns: 7);
        using var claude = Claude(Dir("c4"), Verdict("q1"), special: """{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":52}""");

        await Run(claude, ["q1", "q-special"], new RecordingEvalObserver());

        var failure = Payload().GetProperty("failed_questions").EnumerateArray().Single();
        await Assert.That(failure.GetProperty("code").GetString()).IsEqualTo("iteration_cap");
        await Assert.That(failure.GetProperty("max_iterations").GetInt32()).IsEqualTo(52);
        await Assert.That(failure.GetProperty("turns_total").GetInt32()).IsEqualTo(7);
        await Assert.That(failure.GetProperty("turns_fetched").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task An_error_max_budget_usd_envelope_persists_spend_budget_with_no_detail() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 124_999);
        using var claude = Claude(Dir("c5"), Verdict("q1"), special: """{"type":"result","subtype":"error_max_budget_usd","is_error":true,"num_turns":9}""");

        await Run(claude, ["q1", "q-special"], new RecordingEvalObserver());

        var failure = Payload().GetProperty("failed_questions").EnumerateArray().Single();
        await Assert.That(failure.GetProperty("code").GetString()).IsEqualTo("spend_budget");
        await Assert.That(failure.TryGetProperty("max_iterations", out _)).IsFalse();
        await Assert.That(failure.TryGetProperty("turns_total", out _)).IsFalse();
        await Assert.That(failure.TryGetProperty("http_status", out _)).IsFalse();
    }

    [Test]
    public async Task A_retrieval_harness_that_outlives_its_budget_is_judge_timeout() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeScope(cutoff: 124_999);
        var observer = new RecordingEvalObserver();
        using var claude = Claude(Dir("c6"), Verdict("q1"), before: "sleep 5");
        await using var setup = (await Prepare(claude, observer, TimeProvider.System, Dir("root-6"), "q1"))!;
        setup.RetrievalTimeout = TimeSpan.FromSeconds(1);

        var outcome = await EvalService.RunEvidenceQuestionAsync(setup, _http, _stub.Url, setup.Questions[0], "sonnet", 1, 1, observer, TimeProvider.System, CancellationToken.None);

        await Assert.That(File.Exists(Path.Combine(Dir("c6"), "prompt-0.txt"))).IsTrue();
        await Assert.That(outcome.Failure!.Code).IsEqualTo("judge_timeout");
        await Assert.That(outcome.ScopeMoved).IsFalse();
    }

    [Test]
    public async Task An_unparseable_reply_after_a_budget_stop_is_a_synthesized_insufficient_evidence() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeScope(cutoff: 124_999);
        var observer = new RecordingEvalObserver();
        const string footer = """{"kind":"footer","tool_calls":3,"delivered_bytes":600000,"stop_reason":"byte_budget","sources_refused":[],"ended_at":"2026-09-23T12:00:00+00:00"}""";
        using var claude = Claude(Dir("c7"), Verdict("q1"),
            before: $"printf '%s\\n' '{footer}' >> \"$(ls -d '{Dir("root-7")}'/{EvidenceRunContext.DirectoryPrefix}*)/q1.ledger.jsonl\"");
        File.WriteAllText(Path.Combine(Dir("c7"), "verdict.json"), """{"type":"result","subtype":"success","is_error":false,"num_turns":5,"result":"I could not decide."}""");
        await using var setup = (await Prepare(claude, observer, TimeProvider.System, Dir("root-7"), "q1"))!;

        var outcome = await EvalService.RunEvidenceQuestionAsync(setup, _http, _stub.Url, setup.Questions[0], "sonnet", 1, 1, observer, TimeProvider.System, CancellationToken.None);

        await Assert.That(File.Exists(Path.Combine(Dir("c7"), "prompt-0.txt"))).IsTrue();
        var a = outcome.Assessment!;
        await Assert.That(a.Outcome).IsEqualTo("insufficient_evidence");
        await Assert.That(a.Finding).IsEqualTo("Retrieval stopped on byte_budget and the judge returned no usable verdict.");
        await Assert.That(a.EvidenceCoverage!.StopReason).IsEqualTo("byte_budget");
        await Assert.That(a.EvidenceCoverage.Citations).IsEmpty();
        await Assert.That(a.TraceCoverage!.BudgetTripped).IsTrue();
        await Assert.That(a.Validate()).IsNull();
    }

    [Test]
    public async Task A_question_whose_refresh_left_exactly_its_budget_finishes_with_a_re_open_only_certification_slice() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var time   = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var start  = time.GetUtcNow();
        var scope  = Request.Create().WithPath($"/api/sessions/{Sid}/evidence-scope").WithParam("adopted_children", "false").UsingGet();
        var first  = EvidenceServerStub.Manifest("v1", "tok", null, start, start.AddMinutes(30), [EvidenceServerStub.Source(Root, 0, 124_999, 1)]);
        var second = EvidenceServerStub.Manifest("v1", "tok2", null, start.AddSeconds(1_100), start.AddSeconds(1_100 + 780), [EvidenceServerStub.Source(Root, 0, 124_999, 1)]);
        _stub.Server.Given(scope).InScenario("refresh").WillSetStateTo("resolved").AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithBody(first));
        _stub.Server.Given(scope).InScenario("refresh").WhenStateIs("resolved").AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithBody(second));
        ServeScope(cutoff: 124_999);
        _stub.CursorPage("tok2", second);
        Certify($"{Root}#g1t0");
        var observer = new RecordingEvalObserver();
        using var claude = Claude(Dir("c8"), Verdict("q1", "o1.1"));
        await using var setup = (await Prepare(claude, observer, time, Dir("root-8"), "q1"))!;
        time.Advance(TimeSpan.FromSeconds(1_100));

        var outcome = await EvalService.RunEvidenceQuestionAsync(setup, _http, _stub.Url, setup.Questions[0], "sonnet", 1, 1, observer, time, CancellationToken.None);

        await Assert.That(File.Exists(Path.Combine(Dir("c8"), "prompt-0.txt"))).IsTrue();
        await Assert.That(outcome.ScopeMoved).IsFalse();
        await Assert.That(outcome.Assessment!.EvidenceCoverage!.Citations.Single().Digest).IsEqualTo(Digest);
        await Assert.That(setup.Scope.Refreshes).IsEqualTo(1);
        await Assert.That(_stub.Requests("evidence-citations").Single().RequestMessage.Body!.Contains("\"tok2\"")).IsTrue();
    }

    // Answers the cursor re-opens in order: 200 for the first `ok` of them, then 500 for every later one.
    void ReopenFailsAfter(int ok) {
        var reopen = Request.Create().WithPath($"/api/sessions/{Sid}/evidence-scope").WithParam("cursor", "tok").UsingGet();
        var body   = EvidenceServerStub.Manifest("v1", "tok", null, null, null);
        for (var i = 0; i < ok; i++) {
            var step = _stub.Server.Given(reopen).InScenario("reopens");
            if (i > 0) step = step.WhenStateIs($"s{i}");
            step.WillSetStateTo($"s{i + 1}").AtPriority(1).RespondWith(Response.Create().WithStatusCode(200).WithBody(body));
        }
        _stub.Server.Given(reopen).InScenario("reopens").WhenStateIs($"s{ok}").AtPriority(1).RespondWith(Response.Create().WithStatusCode(500));
    }

    [Test]
    [Arguments("handle")]
    [Arguments("work_budget")]
    [Arguments("elapsed")]
    [Arguments("slice_failed")]
    public async Task Every_citation_that_misses_the_payload_is_reported(string loss) {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        ServeScope(cutoff: 124_999);
        var cited = $"{Root}#g1t0";
        switch (loss) {
            case "handle":       Certify(cited); break;
            case "work_budget":  _stub.Route("POST", "evidence-citations", 200, $$"""{"scope_version":"v1","citations":[{"ref":"{{cited}}","state":"refused","code":"work_budget"}]}"""); break;
            case "elapsed":      _stub.Route("POST", "evidence-citations", 200, "{}", delay: TimeSpan.FromSeconds(5)); break;
            case "slice_failed": Certify(cited); ReopenFailsAfter(1); break;
        }
        var observer = new RecordingEvalObserver();
        using var claude = Claude(Dir("c-loss"), loss == "handle" ? Verdict("q1", "o1.1", "p99.1") : Verdict("q1", "o1.1"));
        await using var setup = (await Prepare(claude, observer, time, Dir("root-loss"), "q1"))!;

        var question = EvalService.RunEvidenceQuestionAsync(setup, _http, _stub.Url, setup.Questions[0], "sonnet", 1, 1, observer, time, CancellationToken.None);
        if (loss == "elapsed") {
            while (_stub.Requests("evidence-scope").Count(e => e.RequestMessage.Query?.ContainsKey("cursor") == true) < 2) await Task.Delay(20);
            while (!question.IsCompleted) { time.Advance(EvidenceCitationClient.CertificationBudget + TimeSpan.FromSeconds(1)); await Task.Delay(50); }
        }
        var outcome = await question;

        await Assert.That(File.Exists(Path.Combine(Dir("c-loss"), "prompt-0.txt"))).IsTrue();
        var notes = observer.Info.Where(i => i.StartsWith("  safety/q1: ", StringComparison.Ordinal)).ToList();
        string[] expected = loss switch {
            "handle"       => [$"  safety/q1: 1 citation(s) dropped: unknown handle or over the {EvidenceBudgets.MaxCitations}-citation cap"],
            "slice_failed" => ["  safety/q1: certification skipped (Failed): failed to resolve the evidence scope: HTTP 500; 1 citation(s) not certified"],
            _              => ["  safety/q1: 1 of 1 citation(s) not certified"]
        };
        await Assert.That(string.Join("\n", notes)).IsEqualTo(string.Join("\n", expected));
        await Assert.That(outcome.Assessment!.EvidenceCoverage!.Citations.Count).IsEqualTo(loss == "handle" ? 1 : 0);
    }

    [Test]
    public async Task A_pre_drain_check_that_fails_discards_the_buffered_fact() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 1); ServeEvents("hello", "world");
        ReopenFailsAfter(2);
        var withFact = Verdict("q1").Replace("\"retain_fact\":null", "\"retain_fact\":\"a recurring pattern\"", StringComparison.Ordinal);
        using var claude = Claude(Dir("c-drain"), withFact);
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["q1"], observer);

        await Assert.That(File.Exists(Path.Combine(Dir("c-drain"), "prompt-1.txt"))).IsTrue();
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Retrospective).IsNotNull();
        await Assert.That(observer.Info).Contains("retained facts discarded: the pre-drain admission check failed (failed to resolve the evidence scope: HTTP 500)");
        await Assert.That(observer.FactsRetained).IsEmpty();
        await Assert.That(_stub.Requests("judge-facts")).IsEmpty();
        await Assert.That(_stub.Requests("evals/v4").Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_refresh_onto_another_version_ends_the_run_with_nothing_posted() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var issued = DateTimeOffset.UtcNow;
        var scope  = Request.Create().WithPath($"/api/sessions/{Sid}/evidence-scope").WithParam("adopted_children", "false").UsingGet();
        _stub.Server.Given(scope).InScenario("moved").WillSetStateTo("resolved").AtPriority(1).RespondWith(Response.Create().WithStatusCode(200)
            .WithBody(EvidenceServerStub.Manifest("v1", "tok", null, issued, issued.AddMinutes(10), [EvidenceServerStub.Source(Root, 0, 124_999, 1)])));
        _stub.Server.Given(scope).InScenario("moved").WhenStateIs("resolved").AtPriority(1).RespondWith(Response.Create().WithStatusCode(200)
            .WithBody(EvidenceServerStub.Manifest("v2", "tok9", null, issued, issued.AddMinutes(30), [EvidenceServerStub.Source(Root, 0, 125_010, 1)])));
        ServeCatalog(); ServeScope(cutoff: 124_999);
        var dir = Dir("c9");
        using var claude = Claude(dir, Verdict("q1"));
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["q1"], observer);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Failures).Contains(EvalService.EvidenceScopeMovedReason);
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(_stub.Requests("judge-facts")).IsEmpty();
        await Assert.That(Directory.GetFiles(dir, "prompt-*")).IsEmpty();
    }

    [Test]
    public async Task A_root_revoked_before_the_next_question_ends_the_run_and_its_buffered_fact_is_never_posted() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var reopen = Request.Create().WithPath($"/api/sessions/{Sid}/evidence-scope").WithParam("cursor", "tok").UsingGet();
        ServeCatalog(); ServeScope(cutoff: 1); ServeEvents("hello", "world");
        _stub.Server.Given(reopen).InScenario("revoke").WillSetStateTo("revoked").AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(EvidenceServerStub.Manifest("v1", "tok", null, null, null)));
        _stub.Server.Given(reopen).InScenario("revoke").WhenStateIs("revoked").AtPriority(1).RespondWith(Response.Create().WithStatusCode(404));
        var withFact = Verdict("q1").Replace("\"retain_fact\":null", "\"retain_fact\":\"a recurring pattern\"", StringComparison.Ordinal);
        using var claude = Claude(Dir("c10"), withFact);
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["q1", "q-special"], observer);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Failures).Contains(EvalService.EvidenceScopeMovedReason);
        await Assert.That(observer.FactsRetained).IsEmpty();
        await Assert.That(_stub.Requests("judge-facts")).IsEmpty();
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
    }

    [Test]
    [Arguments(1L)]
    [Arguments(124_999L)]
    public async Task A_baseline_out_run_leaves_a_ledger_copy_per_question_and_no_run_directory(long cutoff) {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var (root, tmp) = TempRoot();
        using var tmpScope = tmp;
        ServeCatalog(); ServeScope(cutoff); ServeEvents("hello", "world");
        using var claude = Claude(Dir("c11"), Verdict("q1"));
        var outPath  = Dir("baseline.json");
        var baseline = new BaselineObserver(new RecordingEvalObserver(), outPath, Sid, "sonnet", chain: false, TimeProvider.System);

        await Run(claude, ["q1"], baseline);

        using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
        var question = doc.RootElement.GetProperty("questions")[0];
        var ledger   = question.GetProperty("ledger_path").GetString()!;
        await Assert.That(ledger.StartsWith(Path.GetFullPath(outPath + ".ledgers"), StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.ReadLines(ledger).First().Contains("\"question_id\":\"q1\"")).IsTrue();
        await Assert.That(question.GetProperty("route").GetString()).IsEqualTo(cutoff == 1 ? "evidence_one_shot" : "evidence_retrieval");
        await Assert.That(doc.RootElement.GetProperty("treatment").GetProperty("gate_on").GetBoolean()).IsTrue();
        await Assert.That(NoRunDirectory(root)).IsTrue();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("throw")]
    public async Task Every_exit_leaves_no_run_directory(string exit) {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var (root, tmp) = TempRoot();
        using var tmpScope = tmp;
        ServeCatalog(); ServeScope(cutoff: 1);
        if (exit == "throw") _stub.Route("GET", "evidence-events", 200, "not json");
        else ServeEvents("hello", "world");
        using var claude = Claude(Dir("c12"), Verdict("q1"), before: "sleep 5");
        using var cts = new CancellationTokenSource(exit == "cancel" ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan);
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["q1"], observer, ct: cts.Token);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Failures.Count).IsEqualTo(1);
        await Assert.That(NoRunDirectory(root)).IsTrue();
    }

    [Test]
    public async Task The_threshold_has_no_effect_on_the_evidence_route() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog(); ServeScope(cutoff: 1); ServeEvents("hello", "world");
        using var claude = Claude(Dir("c13"), Verdict("q1"));
        var observer = new RecordingEvalObserver();

        await Run(claude, ["q1"], observer, threshold: 10);

        await Assert.That(observer.Completed.Single().Route).IsEqualTo("evidence_one_shot");
        await Assert.That(_stub.Server.LogEntries.Any(e => e.RequestMessage.Query?.ContainsKey("threshold") == true)).IsFalse();
        await Assert.That(_stub.Requests("eval-context")).IsEmpty();
    }

    [Test]
    public async Task A_chain_request_on_an_advertising_server_takes_the_legacy_path_with_a_gate_on_treatment() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        ServeCatalog();
        _stub.Route("GET", "eval-context", 200, LegacyContext);
        _stub.Route("POST", "evals/v4", 200, "{}");
        using var claude = Claude(Dir("c14"), Verdict("q1"));
        var observer = new RecordingEvalObserver();

        await Run(claude, ["q1"], observer, chain: true);

        await Assert.That(_stub.Requests("eval-context").Single().RequestMessage.Query!["chain"].Single()).IsEqualTo("true");
        await Assert.That(_stub.Requests("evidence-scope")).IsEmpty();
        await Assert.That(observer.Treatment!.GateOn).IsTrue();
        await Assert.That(observer.Completed.Single().Route).IsEqualTo("legacy_text");
        await Assert.That(Payload().GetProperty("coverage_policy_version").GetString()).IsEqualTo("coverage-v1");
    }

    [Test]
    public async Task The_advertisement_changes_no_byte_of_a_legacy_request_or_payload() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        var runs = new List<(string Dir, string Body)>();
        foreach (var (advertised, chain, name) in new[] { (false, false, "gate-off"), (false, true, "gate-off-chain"), (true, true, "gate-on-chain") }) {
            _stub.Server.Reset();
            _stub.Route("GET", "eval-context", 200, LegacyContext);
            _stub.Route("POST", "evals/v4", 200, "{}");
            ServeCatalog(advertised);
            var dir = Dir(name);
            using (var claude = Claude(dir, Verdict("q1")))
                await Run(claude, ["q1", "q-tools"], new RecordingEvalObserver(), chain: chain);
            runs.Add((dir, _stub.Requests("evals/v4").Single().RequestMessage.Body!));
        }

        await Assert.That(Sha(runs[0].Body)).IsEqualTo(LegacyV4BodySha);
        foreach (var (dir, body) in runs.Skip(1)) {
            await Assert.That(body).IsEqualTo(runs[0].Body);
            for (var n = 0; n < 3; n++) {
                await Assert.That(Prompt(dir, n)).IsEqualTo(Prompt(runs[0].Dir, n));
                await Assert.That(string.Join("\n", ArgsOf(dir, n))).IsEqualTo(string.Join("\n", ArgsOf(runs[0].Dir, n)));
            }
        }
        var tools = ArgsOf(runs[0].Dir, 1);
        await Assert.That(After(tools, "--max-turns")).IsEqualTo("15");
        await Assert.That(After(tools, "--allowedTools")).IsEqualTo(
            "mcp__kcap-judge__get_session_recap,mcp__kcap-judge__get_session_errors,mcp__kcap-judge__get_transcript,"
          + "mcp__kcap-judge__get_session_summary,mcp__kcap-judge__search_session,mcp__kcap-judge__get_tool_result");
        await Assert.That(After(tools, "--mcp-config").Contains("--run")).IsFalse();
    }

    static string CompletionQuestion(string id, bool marked) =>
        $$"""{"category":"plan_adherence","id":"{{id}}","title":"t","question_text":"Was everything requested done?","prompt":"P {QUESTION_ID}\n{TRACE_JSON}","prompt_version":"3","needs_tools":true,"strategy":"completion","strategy_version":"completion-v1","reports_obligations":{{(marked ? "true" : "false")}}}""";

    // One-shot is out of reach for any real session, so the retrieval arm runs; the orientation and the first view together
    // deliver both events, so a coverage record is mechanically complete.
    const string SmallAd = """{"max_tool_calls":48,"judge_byte_budget_bytes":600000,"page_budget_bytes":65536,"one_shot_limit_chars":100,"retrospective_evidence_bytes":200000,"coverage_policy_version":"coverage-v2"}""";

    void ServeCompletion(params string[] catalogQuestions) {
        _stub.Catalog(SmallAd, "[]", "[" + string.Join(",", catalogQuestions.DefaultIfEmpty(CompletionQuestion("completed_items", marked: true))) + "]");
        ServeScope(cutoff: 1);
        ServeEvents("first", "second");
        _stub.Route("GET", "evidence-first-view", 200,
            "{\"state\":\"built\",\"requested_strategy\":\"completion\",\"strategy\":\"completion\",\"strategy_version\":\"completion-v1\",\"guidance\":\"g\",\"budget_bytes\":196608,"
          + "\"sections\":[{\"purpose\":\"closing_events\",\"kind\":\"events\",\"operation\":\"ReadEventsAsync\",\"budget_bytes\":65536,\"events\":"
          + EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, "first"), EvidenceServerStub.EventEntry(Root, 1, "second")]) + "}],\"omitted_sections\":[],\"limitations\":[]}",
            new Dictionary<string, string> { ["token"] = "tok", ["strategy"] = "completion", ["budget_bytes"] = "196608" });
        _stub.Route("POST", "evidence-citations", 200,
            $$"""{"scope_version":"v1","citations":[{"ref":"{{Root}}@0","state":"certified","digest":"{{Digest}}"},{"ref":"{{Root}}@1","state":"certified","digest":"{{Digest}}"}]}""");
    }

    static string CompletionVerdict(string obligations) =>
        $$"""{"category":"plan_adherence","question_id":"completed_items","outcome":"assessed","score":4,"verdict":"pass","finding":"ok","evidence":null,"recommendation":null,"retain_fact":null,"citations":[],"obligations":{{obligations}} }""";

    [Test]
    public async Task A_reporting_completion_question_persists_certified_obligations_with_its_strategy_stamps() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var dir = Dir("c20");
        ServeCompletion();
        using var claude = Claude(dir, CompletionVerdict("""[{"title":"Write the tests","origin":"plan","status":"verified","anchor":"o3.1","citations":["o3.2"]}]"""));

        await Run(claude, ["completed_items"], new RecordingEvalObserver());

        var q = Payload().GetProperty("categories")[0].GetProperty("questions")[0];
        await Assert.That(q.GetProperty("strategy").GetString()).IsEqualTo("completion");
        await Assert.That(q.GetProperty("strategy_version").GetString()).IsEqualTo("completion-v1");
        var obligation = q.GetProperty("obligations")[0];
        await Assert.That(obligation.GetProperty("id").GetString()).IsEqualTo(EvalObligationRules.DeriveId($"{Root}@0", "Write the tests"));
        await Assert.That(obligation.GetProperty("anchor").GetProperty("ref").GetString()).IsEqualTo($"{Root}@0");
        await Assert.That(obligation.GetProperty("status").GetString()).IsEqualTo("verified");
        await Assert.That(obligation.GetProperty("citations")[0].GetProperty("ref").GetString()).IsEqualTo($"{Root}@1");
        await Assert.That(q.GetProperty("evidence_coverage").GetProperty("citations").GetArrayLength()).IsEqualTo(2);
        await Assert.That(q.TryGetProperty("obligations_not_reported", out _)).IsFalse();
        var prompt = Prompt(dir, 0);
        await Assert.That(prompt.EndsWith("\n" + EvalObligationContract.ReporterMarker, StringComparison.Ordinal)).IsTrue();
        await Assert.That(prompt.Contains("First view (completion):")).IsTrue();
        await Assert.That(After(ArgsOf(dir, 0), "--json-schema")).IsEqualTo(EvalService.EvidenceReportingVerdictJsonSchema);
        await Assert.That(Prompt(dir, 1).Contains("Cited evidence (2):")).IsTrue();
    }

    [Test]
    public async Task An_assessed_checklist_with_no_decisive_entry_and_complete_coverage_persists_as_insufficient_evidence() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var dir = Dir("c21");
        ServeCompletion();
        using var claude = Claude(dir, CompletionVerdict("""[{"title":"Write the tests","origin":"plan","status":"unverified","anchor":"o3.1","citations":[]}]"""));

        await Run(claude, ["completed_items"], new RecordingEvalObserver());

        var q = Payload().GetProperty("categories")[0].GetProperty("questions")[0];
        await Assert.That(q.GetProperty("outcome").GetString()).IsEqualTo("insufficient_evidence");
        await Assert.That(q.TryGetProperty("score", out var score) && score.ValueKind != JsonValueKind.Null).IsFalse();
        await Assert.That(q.TryGetProperty("evidence_coverage", out _)).IsFalse();
        await Assert.That(q.GetProperty("obligations")[0].GetProperty("status").GetString()).IsEqualTo("unverified");
    }

    [Test]
    [Arguments("unparseable")]
    [Arguments("uncertified")]
    public async Task A_lost_checklist_is_named_and_the_verdict_kept(string loss) {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var dir = Dir("c22-" + loss);
        ServeCompletion();
        var obligations = loss == "unparseable"
            ? """[{"title":"Write the tests","origin":"wish","status":"verified","anchor":"o3.1","citations":["o3.2"]}]"""
            : """[{"title":"Write the tests","origin":"plan","status":"verified","anchor":"p9.9","citations":["o3.2"]}]""";
        using var claude = Claude(dir, CompletionVerdict(obligations));

        await Run(claude, ["completed_items"], new RecordingEvalObserver());

        var q = Payload().GetProperty("categories")[0].GetProperty("questions")[0];
        await Assert.That(q.GetProperty("outcome").GetString()).IsEqualTo("assessed");
        await Assert.That(q.GetProperty("obligations_not_reported").GetString()).IsEqualTo(loss);
        await Assert.That(q.TryGetProperty("obligations", out _)).IsFalse();
        await Assert.That(q.GetProperty("strategy").GetString()).IsEqualTo("completion");
    }

    [Test]
    public async Task Two_completion_questions_share_one_first_view_and_only_the_marked_one_reports() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var dir = Dir("c23");
        ServeCompletion(CompletionQuestion("followed_plan", marked: false), CompletionQuestion("completed_items", marked: true));
        using var claude = Claude(dir, CompletionVerdict("""[{"title":"Write the tests","origin":"plan","status":"verified","anchor":"o3.1","citations":["o3.2"]}]"""));

        await Run(claude, ["followed_plan", "completed_items"], new RecordingEvalObserver());

        await Assert.That(_stub.Requests("evidence-first-view").Count).IsEqualTo(1);
        await Assert.That(Prompt(dir, 0).Contains(EvalObligationContract.ReporterMarker)).IsFalse();
        await Assert.That(After(ArgsOf(dir, 0), "--json-schema")).IsEqualTo(EvalService.EvidenceVerdictJsonSchema);
        await Assert.That(Prompt(dir, 1).Contains(EvalObligationContract.ReporterMarker)).IsTrue();
        var questions = Payload().GetProperty("categories")[0].GetProperty("questions").EnumerateArray().ToDictionary(q => q.GetProperty("question_id").GetString()!);
        await Assert.That(questions["followed_plan"].TryGetProperty("obligations", out _)).IsFalse();
        await Assert.That(questions["followed_plan"].GetProperty("strategy").GetString()).IsEqualTo("completion");
        await Assert.That(questions["completed_items"].GetProperty("obligations").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task A_first_view_refused_as_moved_ends_the_run_with_nothing_posted() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        var dir = Dir("c24");
        ServeCompletion();
        _stub.Route("GET", "evidence-first-view", 409, """{"code":"scope_moved","current_version":"v2"}""", priority: 1);
        using var claude = Claude(dir, CompletionVerdict("null"));
        var observer = new RecordingEvalObserver();

        var result = await Run(claude, ["completed_items"], observer);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Failures).Contains(EvalService.EvidenceScopeMovedReason);
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(File.Exists(Path.Combine(dir, "prompt-0.txt"))).IsFalse();
    }
}
