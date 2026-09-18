using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Thin CLI adapter over <see cref="EvalService"/>: parses arg flags,
/// provides a stderr-logging observer, renders the final aggregate as a
/// terminal report. The eval pipeline itself lives in the Eval library so
/// the daemon (DEV-1440 milestone 2) can reuse it.
/// </summary>
class EvalCommand(ProfileContext profiles, HarnessRegistry harnesses, ICapacitorHttpClient http, TimeProvider time) {
    public async Task<int> HandleEval(
            string  sessionId,
            string  model,
            bool    chain,
            int?    thresholdBytes,
            string? questionsCsv,
            string? skipCsv
        ) {
        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await http.ForCommandAsync();

        // Fetch taxonomy once up-front so --list and --questions/--skip share
        // the same source of truth; the server controls it (PR 1), not the CLI.
        var observer = new ConsoleEvalObserver(sessionId, time);
        var catalog  = await EvalQuestionCatalogClient.FetchAsync(baseUrl, httpClient, observer, time, CancellationToken.None);
        if (catalog is null || catalog.Length == 0) {
            // FetchAsync emitted OnFailed with a reason already.
            return 1;
        }

        var include = Parse(questionsCsv);
        var skip    = Parse(skipCsv);
        var (questions, error) = EvalQuestionSelection.Resolve(catalog, include, skip);
        if (error is not null) {
            await Console.Error.WriteLineAsync($"eval: {error}");
            return 2;
        }
        if (questions!.Count == 0) {
            await Console.Error.WriteLineAsync("eval: selection resolved to zero questions");
            return 2;
        }

        var result = await EvalService.RunAsync(
            baseUrl, httpClient, profiles.Resolution.Profile, harnesses, sessionId, model, chain, thresholdBytes,
            observer, time, questions: questions
        );

        if (result is null) return 1;

        Render(result, sessionId);
        return 0;
    }

    public async Task<int> HandleListQuestions() {
        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await http.ForCommandAsync();
        var observer = new ConsoleEvalObserver(sessionId: "", time);
        var catalog  = await EvalQuestionCatalogClient.FetchAsync(baseUrl, httpClient, observer, time, CancellationToken.None);
        if (catalog is null) return 1;

        foreach (var group in catalog.GroupBy(q => q.Category)) {
            Console.WriteLine(group.Key);
            foreach (var q in group) {
                Console.WriteLine($"  {q.Id,-26}  {q.Text}");
            }
        }
        return 0;
    }

    // Distinguishes "flag absent" (null) from "flag present, empty value"
    // (empty array). An explicit --questions "" or --questions "," flows
    // into Resolve() as zero tokens, which produces an empty selection —
    // HandleEval then exits 2 with "selection resolved to zero questions"
    // rather than silently running the full catalog.
    internal static IReadOnlyList<string>? Parse(string? csv) {
        return csv?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static void Render(SessionEvalCompletedPayloadV4 agg, string sessionId) {
        var output = Console.Out;
        output.WriteLine();
        output.WriteLine($"Eval results for session {sessionId}");
        output.WriteLine($"Model: {agg.JudgeModel}   Run: {agg.EvalRunId}");
        output.WriteLine(new string('─', 72));

        foreach (var cat in agg.Categories) {
            output.WriteLine();
            var categoryScore = cat.Score is { } cs ? $"{cs}/5" : "–/5";
            output.WriteLine($"  {cat.Name,-16}  {categoryScore}  [{cat.Verdict ?? "unscored"}]");

            foreach (var q in cat.Questions) {
                var marker = q.Outcome switch {
                    EvalOutcomes.InsufficientEvidence => "?",
                    EvalOutcomes.NotApplicable        => "–",
                    _ => q.Verdict switch { "pass" => "✓", "warn" => "!", _ => "✗" }
                };

                if (q.Outcome == EvalOutcomes.Assessed) {
                    output.WriteLine($"    {marker} {q.QuestionId,-26} {q.Score}  {q.Finding}");
                } else {
                    output.WriteLine($"    {marker} {q.QuestionId,-26} {q.Outcome}: {q.Finding}");
                }

                if (!string.IsNullOrEmpty(q.Evidence)) {
                    output.WriteLine($"        evidence: {q.Evidence}");
                }

                if (q.EvidenceCoverage is { } coverage && !coverage.IsComplete) {
                    var parts = new List<string>();
                    if (coverage.StopReason is not null) parts.Add(coverage.StopReason);
                    parts.AddRange(coverage.Omissions.Select(o => $"{o.Kind}×{o.Count}"));
                    if (coverage.SourcesUnavailable.Count > 0) parts.Add($"{coverage.SourcesUnavailable.Count} sources unavailable");
                    output.WriteLine($"        coverage: {string.Join("; ", parts)}");
                }

                if (q.EvidenceCoverage is { Citations.Count: > 0 } cited) {
                    output.WriteLine($"        cites: {string.Join(", ", cited.Citations.Select(c => c.Ref))}");
                }
            }
        }

        output.WriteLine();
        output.WriteLine(new string('─', 72));
        output.WriteLine(agg.OverallScore is { } overall
            ? $"  Overall: {overall}/5  [{EvalService.VerdictForScore(overall)}]"
            : $"  Overall: not scored ({agg.AssessedQuestions}/{agg.JudgedQuestions} assessed)");
        output.WriteLine($"  {agg.Summary}");
        output.WriteLine();
    }

    /// <summary>
    /// Renders every eval progress callback to stderr with a consistent
    /// <c>[HH:mm:ss] [eval] …</c> prefix, matching the pre-refactor shape
    /// of <c>kcap eval</c>'s output.
    /// </summary>
    sealed class ConsoleEvalObserver(string sessionId, TimeProvider time) : IEvalObserver {
        public void OnInfo(string message) => Log(message);

        public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) =>
            Log($"Evaluating session {sessionId} (run {evalRunId}, model {judgeModel}, {totalQuestions} questions)");

        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
            Log($"Fetched {traceEntries} trace entries ({toolResultsTotal} tool results, {toolResultsTruncated} truncated, {bytesSaved} bytes saved). Trace size: {traceChars} chars.");

        public void OnQuestionStarted(int index, int total, string category, string questionId) =>
            Log($"[{index}/{total}] {category}/{questionId}...");

        public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, long inputTokens, long outputTokens) =>
            Log($"  {assessment.QuestionId} done (input={inputTokens}, output={outputTokens})");

        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) =>
            Log($"  {questionId} failed: {reason}");

        public void OnFactRetained(string category, string fact) =>
            Log($"  retained fact for category {category}");

        public void OnRetrospectiveStarted() =>
            Log("  Synthesising retrospective…");

        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective) =>
            Log($"  Retrospective: {retrospective.OverallSummary}");

        public void OnRetrospectiveFailed(string reason) =>
            Log($"  Retrospective failed: {reason}");

        public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) =>
            Log("Eval result persisted.");

        public void OnFailed(string reason) =>
            Console.Error.WriteLine(reason);

        void Log(string message) =>
            Console.Error.WriteLine($"[{time.GetLocalNow():HH:mm:ss}] [eval] {message}");
    }
}
