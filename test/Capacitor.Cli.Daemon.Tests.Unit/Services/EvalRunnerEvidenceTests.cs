using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The daemon's evidence branch: prepare answers the route and scope fields (legacy under chain) and caches no trace,
/// questions carry their usage, a moved scope or a lost certification answers the run-fatal failure and leaves nothing behind,
/// retained facts are posted only after a clean retrospective, and each phase stops itself inside the server's deadline.
/// The harness probe searches only the fake claude's directory, so no test can reach a real one.</summary>
[NotInParallel]
public class EvalRunnerEvidenceTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    readonly EvidenceServerStub _stub = new();

    public void Dispose() => _stub.Dispose();

    static string Root => EvidenceServerStub.RootSource;
    static string Sid  => EvidenceServerStub.SessionId;

    const string Ad = """{"max_tool_calls":48,"judge_byte_budget_bytes":600000,"page_budget_bytes":65536,"one_shot_limit_chars":400000,"retrospective_evidence_bytes":200000,"coverage_policy_version":"coverage-v2"}""";
    const string CatalogQuestions = """[{"category":"safety","id":"q1","title":"t","question_text":"Was anything destroyed?","prompt":"ONE-SHOT {QUESTION_ID} {TASKS}\n{TRACE_JSON}","prompt_version":"3","needs_tools":false}]""";
    const string Retro = """{"overall":"fine","strengths":[],"issues":[],"suggestions":[]}""";

    sealed class NoopLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    string RunRoot => Tmp.PathTo("runs");

    /// <summary>The system clock, until told to fail: then every read of the time is an OperationCanceledException that no
    /// phase budget and no shutdown caused.</summary>
    sealed class FailingClock : TimeProvider {
        public bool Fail { get; set; }
        public override DateTimeOffset GetUtcNow() => Fail ? throw new OperationCanceledException("not the phase budget") : base.GetUtcNow();
    }

    (EvalRunner Runner, ServerConnection Connection, EvalContextCache Cache) Daemon(FakeClaudeOnPath? claude = null, TimeSpan? question = null, TimeSpan? finalize = null,
            TimeProvider? time = null) {
        var config     = new DaemonConfig { Name = "t", ServerUrl = _stub.Url, ConfigRoot = Config.Root, Profiles = Resolutions.At(_stub.Url, Config.Root) };
        var connection = new ServerConnection(config, AuthFixtures.NewTokenStore(Config.Root), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var cache      = new EvalContextCache(TimeProvider.System);
        var probe      = claude is null ? TestBinaries.None : BinaryProbe.Searching(claude.BinDirectory);
        var runner = new EvalRunner(connection, cache, TestHarnesses.Under(Home, probe), config, new FixedCapacitorHttpClient(), new NoopLifetime(),
            NullLogger<EvalRunner>.Instance, time ?? TimeProvider.System) {
            QuestionPhaseBudget = question ?? EvidencePhaseTimeouts.DaemonQuestion,
            FinalizePhaseBudget = finalize ?? EvidencePhaseTimeouts.DaemonFinalize,
            TempRoot            = RunRoot
        };
        return (runner, connection, cache);
    }

    void Serve(long cutoff, bool advertised = true) {
        _stub.Catalog(advertised ? Ad : null, "[]", CatalogQuestions);
        var issued   = DateTimeOffset.UtcNow;
        var manifest = EvidenceServerStub.Manifest("v1", "tok", null, issued, issued.AddMinutes(30), [EvidenceServerStub.Source(Root, 0, cutoff, 1)]);
        _stub.FreshScope(manifest);
        _stub.CursorPage("tok", manifest);
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, cutoff)]));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 0, "hello"), EvidenceServerStub.EventEntry(Root, 1, "world")]));
        _stub.Route("POST", "evals/v4", 200, "{}");
        _stub.Route("POST", "judge-facts", 200, "{}");
    }

    static string Envelope(string structured) =>
        $$"""{"type":"result","subtype":"success","is_error":false,"num_turns":3,"structured_output":{{structured}},"modelUsage":{"claude-sonnet":{"inputTokens":10,"outputTokens":5} } }""";

    static string Verdict(string? retain = null, params string[] citations) =>
        $$"""{"category":"safety","question_id":"q1","outcome":"assessed","score":4,"verdict":"pass","finding":"ok","evidence":null,"recommendation":null,"retain_fact":{{(retain is null ? "null" : "\"" + retain + "\"")}},"citations":[{{string.Join(",", citations.Select(c => "\"" + c + "\""))}}]}""";

    FakeClaudeOnPath Claude(string verdict, string before = "", string retroBefore = "") {
        var dir = Tmp.PathTo("claude");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "verdict.json"), Envelope(verdict));
        File.WriteAllText(Path.Combine(dir, "retro.json"), Envelope(Retro));
        return new FakeClaudeOnPath($"""
            #!/bin/sh
            d='{dir}'
            n=$(ls "$d" | grep -c '^prompt-')
            cat > "$d/prompt-$n.txt"
            if grep -q '^Retro ' "$d/prompt-$n.txt"; then {retroBefore}
            cat "$d/retro.json"; exit 0; fi
            {before}
            cat "$d/verdict.json"
            """);
    }

    static PrepareEvalCommand Prepare(bool chain = false) =>
        new("run-1", Sid, "sonnet", chain, null, [new EvalQuestionDto { Category = "safety", Id = "q1", Text = "q1", Prompt = "q1" }]);

    static RunQuestionCommand Question() => new("run-1", new EvalQuestionDto { Category = "safety", Id = "q1", Text = "q1", Prompt = "q1" }, 1, 1);

    bool NoRunDirectory() => !Directory.Exists(RunRoot) || Directory.GetDirectories(RunRoot, EvidenceRunContext.DirectoryPrefix + "*").Length == 0;

    string[] Prompts() => Directory.GetFiles(Tmp.PathTo("claude"), "prompt-*");

    [Test]
    [Arguments(1L, "evidence_one_shot")]
    [Arguments(124_999L, "evidence_retrieval")]
    public async Task Prepare_answers_the_route_and_scope_fields_and_caches_no_parsed_trace(long cutoff, string route) {
        Serve(cutoff);
        var (_, connection, cache) = Daemon();

        var result = await connection.PrepareEvalHandler!(Prepare());

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Route).IsEqualTo(route);
        await Assert.That(result.EvidenceScopeVersion).IsEqualTo("v1");
        await Assert.That(result.SourceCount).IsEqualTo(1);
        await Assert.That(result.ExpiresAt).IsNotNull();
        await Assert.That(cache.Get("run-1")).IsNull();
        using var lease = cache.LeaseEvidence("run-1")!;
        await Assert.That(lease.Setup.Trace.TraceJson.Length == 0).IsEqualTo(route == "evidence_retrieval");
        await Assert.That(_stub.Requests("eval-context")).IsEmpty();
    }

    const string LegacyContext = """{"session_id":"0123456789abcdef0123456789abcdef","session_chain":["0123456789abcdef0123456789abcdef"],"trace":[{"kind":"user","timestamp":"2026-09-23T12:00:00Z","text":"hi"}],"compaction":{"threshold_bytes":2000,"entries":1,"tool_results_total":0,"tool_results_truncated":0,"bytes_saved":0,"plan_discovery_degraded":false,"skipped_streams":0,"plan_artifacts_truncated":0,"plan_artifacts_unavailable":0,"plan_artifacts_dropped":0}}""";

    [Test]
    public async Task A_chain_prepare_on_an_advertising_server_answers_legacy() {
        Serve(1);
        _stub.Route("GET", "eval-context", 200, LegacyContext);
        var (_, connection, cache) = Daemon();

        var result = await connection.PrepareEvalHandler!(Prepare(chain: true));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Route).IsEqualTo("legacy");
        await Assert.That(result.EvidenceScopeVersion).IsNull();
        await Assert.That(cache.Get("run-1")).IsNotNull();
        await Assert.That(_stub.Requests("evidence-scope")).IsEmpty();
    }

    [Test]
    public async Task A_question_carries_the_harness_usage() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict());
        var (_, connection, _) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Assessment).IsNotNull();
        await Assert.That(result.InputTokens).IsEqualTo(10);
        await Assert.That(result.OutputTokens).IsEqualTo(5);
        await Assert.That(result.RunFailure).IsNull();
    }

    [Test]
    public async Task A_legacy_question_carries_the_harness_usage() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1, advertised: false);
        _stub.Route("GET", "eval-context", 200, LegacyContext);
        using var claude = Claude(Verdict());
        var (_, connection, _) = Daemon(claude);
        var prepared = await connection.PrepareEvalHandler!(Prepare());

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(prepared.Route).IsEqualTo("legacy");
        await Assert.That(result.Assessment).IsNotNull();
        await Assert.That(result.InputTokens).IsEqualTo(10);
        await Assert.That(result.OutputTokens).IsEqualTo(5);
    }

    [Test]
    public async Task A_root_revoked_before_the_question_answers_scope_moved_and_leaves_nothing_behind() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict());
        var (_, connection, cache) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        _stub.CursorPage("tok", "", 404, priority: 1);

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(result.RunFailure).IsEqualTo("scope_moved");
        await Assert.That(result.Assessment).IsNull();
        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.Error).IsEqualTo(EvalService.EvidenceScopeMovedReason);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(NoRunDirectory()).IsTrue();
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(Prompts()).IsEmpty();
    }

    [Test]
    public async Task A_revocation_during_certification_is_run_fatal_with_nothing_certified() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        _stub.Route("POST", "evidence-citations", 404, "");
        using var claude = Claude(Verdict(null, "e0"));
        var (_, connection, cache) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(_stub.Requests("evidence-citations").Count).IsEqualTo(1);
        await Assert.That(result.RunFailure).IsEqualTo("scope_moved");
        await Assert.That(result.Assessment).IsNull();
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(NoRunDirectory()).IsTrue();
    }

    [Test]
    public async Task A_retained_fact_is_posted_only_after_a_clean_retrospective() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict("a recurring pattern"));
        var (_, connection, _) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        var question = await connection.RunQuestionV2Handler!(Question());

        var finalize = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [question.Assessment!], [], "sonnet"));

        await Assert.That(finalize.Success).IsTrue();
        var fact  = _stub.Requests("judge-facts").Single();
        var retro = Prompts().Select(f => (File: f, Text: File.ReadAllText(f))).Single(p => p.Text.Contains("\nRetro ", StringComparison.Ordinal));
        await Assert.That(fact.RequestMessage.DateTime.ToUniversalTime()).IsGreaterThanOrEqualTo(File.GetLastWriteTimeUtc(retro.File));
        await Assert.That(_stub.Requests("evals/v4").Count).IsEqualTo(1);
        await Assert.That(NoRunDirectory()).IsTrue();
    }

    [Test]
    public async Task A_scope_moved_at_the_retrospective_posts_neither_the_fact_nor_the_eval() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict("a recurring pattern"));
        var (_, connection, _) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        var question = await connection.RunQuestionV2Handler!(Question());
        _stub.CursorPage("tok", "", 409, priority: 1);

        var finalize = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [question.Assessment!], [], "sonnet"));

        await Assert.That(question.Assessment).IsNotNull();
        await Assert.That(finalize.Success).IsFalse();
        await Assert.That(_stub.Requests("judge-facts")).IsEmpty();
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(NoRunDirectory()).IsTrue();
    }

    [Test]
    public async Task A_question_phase_that_trips_its_budget_is_judge_timeout_with_no_later_request() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict(null, "e0"), before: "sleep 5");
        var (_, connection, _) = Daemon(claude, question: TimeSpan.FromSeconds(1));
        await connection.PrepareEvalHandler!(Prepare());

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(result.Failure?.Code).IsEqualTo(EvalFailureCodes.JudgeTimeout);
        await Assert.That(result.RunFailure).IsNull();
        await Assert.That(Prompts().Length).IsEqualTo(1);
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(_stub.Requests("evidence-citations")).IsEmpty();
    }

    [Test]
    public async Task A_finalize_phase_that_trips_its_budget_skips_the_post() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict(), retroBefore: "sleep 5;");
        var (_, connection, _) = Daemon(claude, finalize: TimeSpan.FromSeconds(1));
        await connection.PrepareEvalHandler!(Prepare());
        var question = await connection.RunQuestionV2Handler!(Question());

        var finalize = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [question.Assessment!], [], "sonnet"));

        await Assert.That(question.Assessment).IsNotNull();
        await Assert.That(finalize.Success).IsFalse();
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
        await Assert.That(NoRunDirectory()).IsTrue();
    }

    [Test]
    public async Task A_cancellation_the_question_budget_did_not_cause_is_chat_error_not_judge_timeout() {
        Serve(1);
        var clock = new FailingClock();
        var (_, connection, _) = Daemon(time: clock);
        await connection.PrepareEvalHandler!(Prepare());
        clock.Fail = true;

        var result = await connection.RunQuestionV2Handler!(Question());

        await Assert.That(result.Failure?.Code).IsEqualTo(EvalFailureCodes.ChatError);
        await Assert.That(result.Error).IsEqualTo("OperationCanceledException: not the phase budget");
    }

    [Test]
    public async Task A_cancellation_the_finalize_budget_did_not_cause_is_not_reported_as_that_budget() {
        Serve(1);
        var clock = new FailingClock();
        var (_, connection, _) = Daemon(time: clock);
        await connection.PrepareEvalHandler!(Prepare());
        clock.Fail = true;

        var finalize = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [new EvalQuestionAssessment {
            Category = "safety", QuestionId = "q1", Outcome = EvalOutcomes.Assessed, Score = 4, Verdict = "pass", Finding = "ok" }], [], "sonnet"));

        await Assert.That(finalize.Success).IsFalse();
        await Assert.That(finalize.Error).IsEqualTo("OperationCanceledException: not the phase budget");
    }

    [Test]
    public async Task One_assessed_and_one_iteration_cap_persist_together_and_all_failed_posts_nothing() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict());
        var (_, connection, _) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        var question = await connection.RunQuestionV2Handler!(Question());
        var cap = new EvalQuestionFailure { Category = "safety", QuestionId = "q2", Code = EvalFailureCodes.IterationCap, MaxIterations = 52, TurnsFetched = 1, TurnsTotal = 3 };

        var both = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [question.Assessment!], [cap], "sonnet"));

        await Assert.That(both.Success).IsTrue();
        using var payload = JsonDocument.Parse(_stub.Requests("evals/v4").Single().RequestMessage.Body!);
        await Assert.That(payload.RootElement.GetProperty("failed_questions")[0].GetProperty("code").GetString()).IsEqualTo("iteration_cap");
        await Assert.That(payload.RootElement.GetProperty("categories")[0].GetProperty("questions").GetArrayLength()).IsEqualTo(1);

        await connection.PrepareEvalHandler!(Prepare());
        var none = await connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [], [cap with { QuestionId = "q1" }], "sonnet"));
        await Assert.That(none.Success).IsFalse();
        await Assert.That(_stub.Requests("evals/v4").Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_cancel_during_a_question_stops_it_and_removes_the_run_only_once_it_has_ended() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict(), before: "sleep 20");
        var (_, connection, cache) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        var running = connection.RunQuestionV2Handler!(Question());
        for (var i = 0; i < 200 && Prompts().Length == 0; i++) await Task.Delay(50);

        await connection.CancelEvalHandler!(new CancelEvalCommand("run-1"));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(Prompts().Length).IsEqualTo(1);
        await Assert.That(result.Assessment).IsNull();
        await Assert.That(result.Failure?.Code).IsEqualTo(EvalFailureCodes.ChatError);
        await Assert.That(result.Error).IsEqualTo("the eval run was cancelled or replaced");
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(NoRunDirectory()).IsTrue();
    }

    [Test]
    public async Task A_finalize_cancelled_by_a_second_prepare_leaves_the_replacement_run_in_place() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        Serve(1);
        using var claude = Claude(Verdict(), retroBefore: "sleep 20;");
        var (_, connection, cache) = Daemon(claude);
        await connection.PrepareEvalHandler!(Prepare());
        var question = await connection.RunQuestionV2Handler!(Question());
        var finalizing = connection.FinalizeEvalV2Handler!(new FinalizeEvalV2Command("run-1", [question.Assessment!], [], "sonnet"));
        for (var i = 0; i < 200 && Prompts().Length < 2; i++) await Task.Delay(50);

        await connection.PrepareEvalHandler!(Prepare());
        var finalized = await finalizing.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(Prompts().Length).IsEqualTo(2);
        await Assert.That(finalized.Success).IsFalse();
        await Assert.That(cache.Count).IsEqualTo(1);
        using var replacement = cache.LeaseEvidence("run-1");
        await Assert.That(replacement).IsNotNull();
        await Assert.That(replacement!.Cancelled.IsCancellationRequested).IsFalse();
        await Assert.That(Directory.Exists(replacement.Setup.Context.RunDirectory)).IsTrue();
        await Assert.That(Directory.GetDirectories(RunRoot, EvidenceRunContext.DirectoryPrefix + "*")).IsEquivalentTo([replacement.Setup.Context.RunDirectory]);
        await Assert.That(_stub.Requests("evals/v4")).IsEmpty();
    }

    [Test]
    public async Task The_protocol_one_question_handler_never_reads_an_evidence_run() {
        Serve(1);
        var (_, connection, _) = Daemon();
        await connection.PrepareEvalHandler!(Prepare());

        var result = await connection.RunQuestionHandler!(Question());

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsEqualTo("context not cached (prepare missing or expired)");
    }

    [Test]
    public async Task The_phase_budgets_sit_one_rpc_margin_inside_the_servers_route_keyed_deadlines() {
        await Assert.That(EvidencePhaseTimeouts.DaemonQuestion).IsEqualTo(TimeSpan.FromSeconds(840 - 60));
        await Assert.That(EvidencePhaseTimeouts.DaemonFinalize).IsEqualTo(TimeSpan.FromSeconds(1_080 - 60));
    }
}
