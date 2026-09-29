using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class BaselineObserverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Records_completed_and_failed_questions_and_still_calls_the_inner_terminal() {
        var inner = new RecordingObserver();
        var path  = Tmp.PathTo("baseline.json");
        var sut   = new BaselineObserver(inner, path, "sess-1", "sonnet", chain: false, TimeProvider.System);

        sut.OnStarted("run-1", "sonnet", 2);
        sut.OnQuestionCompleted(
            1, 2,
            new EvalQuestionAssessment { Category = "safety", QuestionId = "ok_q", Finding = "fine" },
            new EvalUsage { InputTokens = 10, OutputTokens = 5 },
            "text", TimeSpan.FromMilliseconds(120), runnerInvocations: 1);
        sut.OnQuestionFailed(2, 2, "safety", "failed_q", "null judge result");
        sut.OnFinished(Aggregate());

        await Assert.That(sut.WriteFailed).IsFalse();
        await Assert.That(inner.FinishedCount).IsEqualTo(1);

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var       root = doc.RootElement;

        var questions = root.GetProperty("questions");
        await Assert.That(questions.GetArrayLength()).IsEqualTo(1);
        await Assert.That(questions[0].GetProperty("question_id").GetString()).IsEqualTo("ok_q");

        var failures = root.GetProperty("failures");
        await Assert.That(failures.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failures[0].GetProperty("question_id").GetString()).IsEqualTo("failed_q");
        await Assert.That(failures[0].GetProperty("category").GetString()).IsEqualTo("safety");
        await Assert.That(failures[0].GetProperty("reason").GetString()).IsEqualTo("null judge result");
    }

    [Test]
    public async Task A_failed_write_is_surfaced_rather_than_reported_as_success() {
        var inner   = new RecordingObserver();
        var blocker = Tmp.CreateFile("blocker.txt", "x");
        // A path whose parent component is a regular file cannot be written to on any platform.
        var badPath = Path.Combine(blocker, "baseline.json");
        var sut     = new BaselineObserver(inner, badPath, "sess-1", "sonnet", chain: false, TimeProvider.System);

        sut.OnFinished(Aggregate());

        await Assert.That(sut.WriteFailed).IsTrue();
        await Assert.That(inner.FinishedCount).IsEqualTo(1);
        await Assert.That(inner.Info.Any(m => m.Contains("failed to write baseline output"))).IsTrue();
    }

    static SessionEvalCompletedPayloadV4 Aggregate() => new() {
        EvalRunId             = "run-1",
        JudgeModel            = "sonnet",
        Summary               = "done",
        AssessedQuestions     = 1,
        UnassessedQuestions   = 0,
        JudgedQuestions       = 1,
        TotalQuestions        = 2,
        CoveragePolicyVersion = "v1",
    };

    static EvalQuestionAssessment Assessed(string questionId) => new() { Category = "safety", QuestionId = questionId, Finding = "f" };

    static readonly EvalTreatment GateOn = new() {
        GateOn = true, Budgets = new() { ["MaxToolCalls"] = 48 }, PreambleHashes = new() { ["preamble-eval-oneshot.txt"] = new string('a', 64) }
    };

    [Test]
    public async Task The_treatment_and_a_copied_ledger_are_recorded_and_the_copy_outlives_the_original() {
        var inner  = new RecordingEvalObserver();
        var path   = Tmp.PathTo("baseline.json");
        var ledger = Tmp.CreateFile("q1.ledger.jsonl", "{\"kind\":\"header\",\"question_id\":\"safety/q1\"}\n");
        var sut    = new BaselineObserver(inner, path, "sess-1", "sonnet", chain: false, TimeProvider.System);

        sut.OnTreatment(GateOn);
        sut.OnStarted("run-1", "sonnet", 1);
        sut.OnQuestionCompleted(1, 1, Assessed("safety/q1"), new EvalUsage(), "evidence_retrieval", TimeSpan.Zero, 1);
        sut.OnQuestionLedger(1, "safety/q1", ledger);
        File.Delete(ledger);
        sut.OnFinished(Aggregate());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("treatment").GetProperty("gate_on").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("treatment").GetProperty("budgets").GetProperty("MaxToolCalls").GetInt64()).IsEqualTo(48);
        var question = root.GetProperty("questions")[0];
        await Assert.That(question.GetProperty("route").GetString()).IsEqualTo("evidence_retrieval");
        var hash   = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("safety/q1")))[..16];
        var copied = question.GetProperty("ledger_path").GetString()!;
        await Assert.That(copied).IsEqualTo(Path.GetFullPath(Path.Combine(path + ".ledgers", $"001-{hash}.jsonl")));
        await Assert.That((await File.ReadAllTextAsync(copied)).Contains("safety/q1")).IsTrue();
        await Assert.That(inner.Treatment).IsEqualTo(GateOn);
        await Assert.That(inner.Ledgers.Single().Path).IsEqualTo(ledger);
    }

    [Test]
    [Arguments("../x")]
    [Arguments("a/b\\c")]
    [Arguments("/etc/passwd")]
    [Arguments("C:\\x")]
    [Arguments("con<>:\"|?*\u0001")]
    public async Task A_hostile_question_id_lands_inside_the_ledger_directory_under_its_hashed_name(string questionId) {
        var path   = Tmp.PathTo("baseline.json");
        var ledger = Tmp.CreateFile("q.ledger.jsonl", "{}\n");
        var sut    = new BaselineObserver(new RecordingObserver(), path, "s", "sonnet", chain: false, TimeProvider.System);

        sut.OnQuestionCompleted(7, 9, Assessed(questionId), new EvalUsage(), "evidence_one_shot", TimeSpan.Zero, 1);
        sut.OnQuestionLedger(7, questionId, ledger);
        sut.OnFinished(Aggregate());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var copied = doc.RootElement.GetProperty("questions")[0].GetProperty("ledger_path").GetString()!;
        await Assert.That(copied.StartsWith(Path.GetFullPath(path + ".ledgers") + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsTrue();
        await Assert.That(Regex.IsMatch(Path.GetFileName(copied), "^007-[0-9a-f]{16}\\.jsonl$")).IsTrue();
        await Assert.That(File.Exists(copied)).IsTrue();
    }

    [Test]
    public async Task The_same_question_id_at_two_ordinals_gets_two_files() {
        var path = Tmp.PathTo("baseline.json");
        var sut  = new BaselineObserver(new RecordingObserver(), path, "s", "sonnet", chain: false, TimeProvider.System);

        sut.OnQuestionCompleted(1, 2, Assessed("dup"), new EvalUsage(), "evidence_retrieval", TimeSpan.Zero, 1);
        sut.OnQuestionLedger(1, "dup", Tmp.CreateFile("a.jsonl", "first\n"));
        sut.OnQuestionCompleted(2, 2, Assessed("dup"), new EvalUsage(), "evidence_retrieval", TimeSpan.Zero, 1);
        sut.OnQuestionLedger(2, "dup", Tmp.CreateFile("b.jsonl", "second\n"));
        sut.OnFinished(Aggregate());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var paths = doc.RootElement.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("ledger_path").GetString()!).ToList();
        await Assert.That(paths.Distinct().Count()).IsEqualTo(2);
        await Assert.That(await File.ReadAllTextAsync(paths[0])).IsEqualTo("first\n");
        await Assert.That(await File.ReadAllTextAsync(paths[1])).IsEqualTo("second\n");
    }

    [Test]
    public async Task A_ledger_that_cannot_be_copied_leaves_no_path_and_one_info_line() {
        var inner = new RecordingObserver();
        var path  = Tmp.PathTo("baseline.json");
        var sut   = new BaselineObserver(inner, path, "s", "sonnet", chain: false, TimeProvider.System);

        sut.OnQuestionCompleted(1, 1, Assessed("q"), new EvalUsage(), "evidence_retrieval", TimeSpan.Zero, 1);
        sut.OnQuestionLedger(1, "q", Tmp.PathTo("missing.jsonl"));
        sut.OnFinished(Aggregate());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        await Assert.That(doc.RootElement.GetProperty("questions")[0].TryGetProperty("ledger_path", out _)).IsFalse();
        await Assert.That(inner.Info.Count(m => m.Contains("failed to retain the ledger"))).IsEqualTo(1);
        await Assert.That(sut.WriteFailed).IsFalse();
    }

    [Test]
    public async Task A_run_that_reports_neither_writes_neither_field() {
        var path = Tmp.PathTo("baseline.json");
        var sut  = new BaselineObserver(new RecordingObserver(), path, "s", "sonnet", chain: false, TimeProvider.System);

        sut.OnQuestionCompleted(1, 1, Assessed("q"), new EvalUsage(), "legacy_text", TimeSpan.Zero, 1);
        sut.OnFinished(Aggregate());

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        await Assert.That(doc.RootElement.TryGetProperty("treatment", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("questions")[0].TryGetProperty("ledger_path", out _)).IsFalse();
    }

    [Test]
    public async Task The_safe_observer_forwards_both_callbacks_and_swallows_a_throw() {
        var recording = new RecordingEvalObserver();
        var forwarded = new EvalService.SafeObserver(recording);
        forwarded.OnTreatment(GateOn);
        forwarded.OnQuestionLedger(3, "q", "/tmp/q3.ledger.jsonl");

        var throwing = new EvalService.SafeObserver(new ThrowingObserver());
        throwing.OnTreatment(GateOn);
        throwing.OnQuestionLedger(1, "q", "x");

        await Assert.That(recording.Treatment).IsEqualTo(GateOn);
        await Assert.That(recording.Ledgers.Single()).IsEqualTo((3, "q", "/tmp/q3.ledger.jsonl"));
    }

    [Test]
    public async Task An_observer_that_overrides_neither_callback_still_runs() {
        IEvalObserver plain = new RecordingObserver();

        plain.OnTreatment(EvalTreatment.None);
        plain.OnQuestionLedger(1, "q", "x");

        await Assert.That(((RecordingObserver)plain).FinishedCount).IsEqualTo(0);
    }

    sealed class ThrowingObserver : IEvalObserver {
        public void OnInfo(string message) { }
        public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) { }
        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) { }
        public void OnQuestionStarted(int index, int total, string category, string questionId) { }
        public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, EvalUsage usage, string route, TimeSpan elapsed, int runnerInvocations) { }
        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) { }
        public void OnFactRetained(string category, string fact) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective, EvalUsage usage, TimeSpan elapsed) { }
        public void OnRetrospectiveFailed(string reason) { }
        public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) { }
        public void OnFailed(string reason) { }
        public void OnTreatment(EvalTreatment treatment) => throw new InvalidOperationException("observer failure");
        public void OnQuestionLedger(int index, string questionId, string tempLedgerPath) => throw new InvalidOperationException("observer failure");
    }

    sealed class RecordingObserver : IEvalObserver {
        public List<string> Info          { get; } = [];
        public int          FinishedCount { get; private set; }
        public int          FailedCount   { get; private set; }

        public void OnInfo(string message) => Info.Add(message);
        public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) { }
        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) { }
        public void OnQuestionStarted(int index, int total, string category, string questionId) { }
        public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, EvalUsage usage, string route, TimeSpan elapsed, int runnerInvocations) { }
        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) { }
        public void OnFactRetained(string category, string fact) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective, EvalUsage usage, TimeSpan elapsed) { }
        public void OnRetrospectiveFailed(string reason) { }
        public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) => FinishedCount++;
        public void OnFailed(string reason) => FailedCount++;
    }
}
