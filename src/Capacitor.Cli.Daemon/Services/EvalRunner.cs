using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Per-phase handlers for the DEV-1463 PR 2 eval dispatch protocol.
/// Replaces the pre-PR-2 single-shot <c>RunEvalCommand</c> handler.
/// Holds no state between calls except via <see cref="EvalContextCache"/>.
///
/// <para>Each handler translates a SignalR client-result invocation into
/// a call to <see cref="EvalService"/> and packages the outcome as a
/// typed result DTO. The server's orchestrator (see
/// <c>EvalRunOrchestrator</c>) threads the phases together.</para>
/// </summary>
internal sealed class EvalRunner {
    readonly TimeProvider         _time;
    readonly ServerConnection     _connection;
    readonly EvalContextCache     _cache;
    readonly ILogger<EvalRunner>  _logger;
    readonly string               _baseUrl;
    readonly CancellationToken    _shutdownToken;
    readonly HarnessRegistry      _harnesses;
    readonly ProfileContext       _profiles;
    readonly ICapacitorHttpClient _http;

    public EvalRunner(
            ServerConnection         connection,
            EvalContextCache         cache,
            HarnessRegistry          harnesses,
            DaemonConfig             config,
            ICapacitorHttpClient     http,
            IHostApplicationLifetime lifetime,
            ILogger<EvalRunner>      logger,
            TimeProvider             time
        ) {
        _time       = time;
        _connection    = connection;
        _cache         = cache;
        _harnesses     = harnesses;
        _profiles      = config.Profiles;
        _http          = http;
        _logger        = logger;
        _baseUrl       = config.ServerUrl.TrimEnd('/');
        _shutdownToken = lifetime.ApplicationStopping;

        _connection.PrepareEvalHandler     = HandlePrepareAsync;
        _connection.RunQuestionHandler     = HandleRunQuestionAsync;
        _connection.FinalizeEvalHandler    = HandleFinalizeAsync;
        _connection.CancelEvalHandler      = HandleCancelAsync;
        _connection.RunQuestionV2Handler   = HandleRunQuestionV2Async;
        _connection.FinalizeEvalV2Handler  = HandleFinalizeV2Async;
    }

    async Task<PrepareResult> HandlePrepareAsync(PrepareEvalCommand cmd) {
        // SignalR 10.0.5's On<T1, TResult> overloads don't surface a
        // per-call CancellationToken, so cancellation here is bounded
        // only by the daemon's shutdown token. Server-side phase timeouts
        // take effect via InvokeAsync<T>'s own timeout unwinding (the
        // daemon's response is simply discarded if the server moved on).
        using var httpClient = await _http.ForBackgroundAsync(_shutdownToken);
        var       observer   = new DaemonEvalObserver(_connection, cmd.EvalRunId, cmd.SessionId, _logger);

        try {
            // Phase 3 — fetch the catalog (rendered prompts + raw text +
            // versions) so PrepareAsync reconciles the run question list from it.
            var catalog = await EvalCatalogClient.FetchAsync(_baseUrl, httpClient, observer, _time, _shutdownToken);
            if (catalog is null) return new(false, "catalog load failed", null, 0, 0, 0, 0, 0);

            var ctx = await EvalService.PrepareAsync(
                _baseUrl,
                httpClient,
                _profiles.Resolution.Profile,
                _harnesses,
                cmd.SessionId,
                cmd.Questions,
                catalog,
                cmd.Chain,
                cmd.ThresholdBytes,
                observer,
                _time,
                _shutdownToken,
                cmd.Model,
                cmd.EvalRunId
            );

            if (ctx is null) return new(false, "context load failed", null, 0, 0, 0, 0, 0);

            _cache.Put(cmd.EvalRunId, ctx);

            return new(
                true,
                null,
                ctx.SessionId,
                ctx.ContextResult.Trace.Count,
                ctx.TraceJson.Length,
                ctx.ContextResult.Compaction.ToolResultsTotal,
                ctx.ContextResult.Compaction.ToolResultsTruncated,
                ctx.ContextResult.Compaction.BytesSaved
            );
        } catch (Exception ex) {
            _logger.LogError(ex, "PrepareEval failed for {RunId}", cmd.EvalRunId);

            return new(false, $"{ex.GetType().Name}: {ex.Message}", null, 0, 0, 0, 0, 0);
        }
    }

    async Task<QuestionResult> HandleRunQuestionAsync(RunQuestionCommand cmd) {
        var ctx = _cache.Get(cmd.EvalRunId);

        if (ctx is null) return new(false, null, "context not cached (prepare missing or expired)", 0, 0);

        using var httpClient = await _http.ForBackgroundAsync(_shutdownToken);
        var       observer   = new DaemonEvalObserver(_connection, cmd.EvalRunId, ctx.SessionId, _logger);

        try {
            // Phase 3 — the SignalR-supplied cmd.Question carries raw text
            // and a null prompt version; the RECONCILED question from the cached
            // context (matched by id) carries the catalog's rendered Prompt, RawText,
            // and PromptVersion. Judge the reconciled item, not the wire DTO.
            var reconciled = ctx.Questions.FirstOrDefault(q => q.Id == cmd.Question.Id);
            if (reconciled is null)
                return new(false, null, $"question '{cmd.Question.Id}' not in reconciled catalog", 0, 0);

            // Model is carried on the cached EvalContext (set during Prepare) —
            // the per-question wire format doesn't repeat it.
            var result = await EvalService.RunQuestionAsync(
                ctx,
                httpClient,
                _baseUrl,
                reconciled,
                ctx.Model,
                cmd.Index,
                cmd.Total,
                observer,
                _time,
                _shutdownToken
            );

            if (result.Assessment is { } a) {
                // The legacy wire has no outcome — a protocol-1 server can only receive a scored
                // verdict, so an unassessed outcome is reported as a failure rather than silently
                // coerced into a fabricated score.
                if (a.Outcome != EvalOutcomes.Assessed)
                    return new(false, null, $"question not assessed: {a.Outcome}", 0, 0);

                var legacy = new EvalQuestionVerdict {
                    Category       = a.Category,
                    QuestionId     = a.QuestionId,
                    Score          = a.Score!.Value,
                    Verdict        = a.Verdict!,
                    Finding        = a.Finding,
                    Evidence       = a.Evidence,
                    Recommendation = a.Recommendation,
                    ToolsUsed      = a.ToolsUsed,
                    PromptVersion  = a.PromptVersion
                };

                return new QuestionResult(true, legacy, null, 0, 0);
            }

            return new(false, null, result.Failure?.Code ?? "verdict null", 0, 0);
        } catch (Exception ex) {
            _logger.LogError(ex, "RunQuestion failed for {RunId}/{QuestionId}", cmd.EvalRunId, cmd.Question.Id);

            return new(false, null, $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    async Task<QuestionResultV2> HandleRunQuestionV2Async(RunQuestionCommand cmd) {
        var ctx = _cache.Get(cmd.EvalRunId);

        if (ctx is null)
            return new QuestionResultV2(null,
                new EvalQuestionFailure { Category = cmd.Question.Category, QuestionId = cmd.Question.Id, Code = EvalFailureCodes.ChatError },
                "context not cached (prepare missing or expired)", 0, 0);

        using var httpClient = await _http.ForBackgroundAsync(_shutdownToken);
        // Silent: on protocol 2 the server's orchestrator is the sole author of per-question
        // progress (it dispatches RunQuestionV2 itself and publishes the result), so relaying
        // per-question events here as well would let a daemon-side timeout overwrite a
        // server-side one already recorded as failed.
        var observer = new DaemonEvalObserver(_connection, cmd.EvalRunId, ctx.SessionId, _logger, silentPerQuestion: true);

        try {
            var reconciled = ctx.Questions.FirstOrDefault(q => q.Id == cmd.Question.Id);
            if (reconciled is null)
                return new QuestionResultV2(null,
                    new EvalQuestionFailure { Category = cmd.Question.Category, QuestionId = cmd.Question.Id, Code = EvalFailureCodes.ChatError },
                    $"question '{cmd.Question.Id}' not in reconciled catalog", 0, 0);

            var result = await EvalService.RunQuestionAsync(
                ctx, httpClient, _baseUrl, reconciled, ctx.Model, cmd.Index, cmd.Total, observer, _time, _shutdownToken);

            return new QuestionResultV2(result.Assessment, result.Failure, result.Failure is null ? null : "judge did not produce a verdict", 0, 0);
        } catch (Exception ex) {
            _logger.LogError(ex, "RunQuestionV2 failed for {RunId}/{QuestionId}", cmd.EvalRunId, cmd.Question.Id);

            return new QuestionResultV2(null,
                new EvalQuestionFailure { Category = cmd.Question.Category, QuestionId = cmd.Question.Id, Code = EvalFailureCodes.ChatError },
                $"{ex.GetType().Name}: {ex.Message}", 0, 0);
        }
    }

    async Task<FinalizeResult> HandleFinalizeV2Async(FinalizeEvalV2Command cmd) {
        var ctx = _cache.Get(cmd.EvalRunId);

        if (ctx is null) return new(false, "context not cached", null);

        using var httpClient = await _http.ForBackgroundAsync(_shutdownToken);
        var       observer   = new DaemonEvalObserver(_connection, cmd.EvalRunId, ctx.SessionId, _logger);

        try {
            var aggregate = await EvalService.FinalizeAsync(
                ctx, httpClient, _baseUrl, cmd.Assessments, cmd.Failures, cmd.Model, observer, _time, _shutdownToken);

            return new(aggregate is not null, aggregate is null ? "finalize failed" : null, null);
        } catch (Exception ex) {
            _logger.LogError(ex, "FinalizeEvalV2 failed for {RunId}", cmd.EvalRunId);

            return new(false, $"{ex.GetType().Name}: {ex.Message}", null);
        } finally {
            _cache.Remove(cmd.EvalRunId);
        }
    }

    async Task<FinalizeResult> HandleFinalizeAsync(FinalizeEvalCommand cmd) {
        var ctx = _cache.Get(cmd.EvalRunId);

        if (ctx is null) return new(false, "context not cached", null);

        using var httpClient = await _http.ForBackgroundAsync(_shutdownToken);
        var       observer   = new DaemonEvalObserver(_connection, cmd.EvalRunId, ctx.SessionId, _logger);

        try {
            // FinalizeAsync signature was updated in Task 6.5 — the taxonomy is
            // carried on ctx.Questions, not passed separately.
            // Phase 3: FinalizeAsync now returns SessionEvalCompletedPayloadV3;
            // FinalizeResult.Aggregate is the V1 wire DTO held by the server
            // orchestrator, which only inspects Success on this result so we
            // pass null for Aggregate. The V3 payload is already persisted to
            // /api/sessions/{id}/evals/v3 inside FinalizeAsync.
            var aggregate = await EvalService.FinalizeAsync(
                ctx,
                httpClient,
                _baseUrl,
                cmd.Verdicts,
                cmd.Model,
                observer,
                _time,
                _shutdownToken
            );

            return new(aggregate is not null, aggregate is null ? "finalize failed" : null, null);
        } catch (Exception ex) {
            _logger.LogError(ex, "FinalizeEval failed for {RunId}", cmd.EvalRunId);

            return new(false, $"{ex.GetType().Name}: {ex.Message}", null);
        } finally {
            // Always evict — a finalize throw must not leak the cached context.
            _cache.Remove(cmd.EvalRunId);
        }
    }

    Task HandleCancelAsync(CancelEvalCommand cmd) {
        _cache.Remove(cmd.EvalRunId);
        _logger.LogInformation("Cancelled eval {RunId}", cmd.EvalRunId);

        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="IEvalObserver"/> implementation that pushes every per-run
/// and per-question transition — <see cref="OnStarted"/>,
/// <see cref="OnQuestionStarted"/>, <see cref="OnQuestionCompleted"/>,
/// <see cref="OnQuestionFailed"/>, <see cref="OnFinished"/>,
/// <see cref="OnFailed"/> — over the daemon's SignalR connection so the
/// dashboard can render live progress. Per-question start + fail are
/// relayed (not just completed) because the dashboard advances the
/// "running" marker off `QuestionStarted` and the ✗ marker off
/// `QuestionFailed`; without them, a judge that returned an unparseable
/// verdict (or timed out) leaves the UI stuck on the previous question
/// until the whole 13-judge loop finally ends. <see cref="OnInfo"/>,
/// <see cref="OnContextFetched"/>, and <see cref="OnFactRetained"/> are
/// debug-log-only — the dashboard has no rendering for them and the
/// SignalR chatter would be pure overhead.
/// </summary>
sealed class DaemonEvalObserver(
        ServerConnection connection,
        string           evalRunId,
        string           sessionId,
        ILogger          logger,
        // On eval protocol 2 the server's orchestrator dispatches RunQuestionV2 itself and is the
        // sole author of per-question progress; relaying here too would let a slow daemon-side
        // completion overwrite a server-side timeout already recorded as failed. Protocol 1 has no
        // other channel, so it always relays (default false).
        bool             silentPerQuestion = false
    ) : IEvalObserver {
    // The dashboard expects EvalStarted → question completions → EvalFinished/EvalFailed in order.
    // A chain rather than a semaphore: relays are fire-and-forget, so there is no point at which a
    // semaphore could be disposed without racing one still in flight.
    readonly Lock _tailLock = new();
    Task _tail = Task.CompletedTask;

    public void OnInfo(string message) =>
        logger.LogDebug("[eval {Run}] {Message}", evalRunId, message);

    public void OnStarted(string runId, string judgeModel, int totalQuestions) {
        logger.LogInformation("Eval {Run} started on session {Sid} (model {Model}, {Count} questions)", runId, sessionId, judgeModel, totalQuestions);
        Relay(() => connection.EvalStartedAsync(runId, sessionId, judgeModel, totalQuestions), "EvalStarted");
    }

    public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
        logger.LogDebug("Eval {Run} context fetched: {Entries} entries, {Chars} chars", evalRunId, traceEntries, traceChars);

    public void OnQuestionStarted(int index, int total, string category, string questionId) {
        logger.LogDebug("[eval {Run}] [{Index}/{Total}] {Category}/{Question} started", evalRunId, index, total, category, questionId);
        if (!silentPerQuestion)
            Relay(() => connection.EvalQuestionStartedAsync(evalRunId, sessionId, index, total, category, questionId), "EvalQuestionStarted");
    }

    public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, long inputTokens, long outputTokens) {
        logger.LogInformation(
            "[eval {Run}] [{Index}/{Total}] {Question} -> {Outcome} {Score} ({Verdict})",
            evalRunId,
            index,
            total,
            assessment.QuestionId,
            assessment.Outcome,
            assessment.Score,
            assessment.Verdict
        );
        if (silentPerQuestion) return;

        // The legacy push has no outcome, so an unassessed question relays as a failure instead
        // of a null-score completion — the protocol-1 RunQuestion RPC already reports it as one
        // (the legacy wire has no representation for "assessed but no score"), and sending both
        // would tell an older server the same question both completed and failed.
        if (assessment.Outcome == EvalOutcomes.Assessed) {
            Relay(() => connection.EvalQuestionCompletedAsync(evalRunId, sessionId, index, total, assessment.Category, assessment.QuestionId, assessment.Outcome, assessment.Score, assessment.Verdict), "EvalQuestionCompleted");
        } else {
            Relay(() => connection.EvalQuestionFailedAsync(evalRunId, sessionId, index, total, assessment.Category, assessment.QuestionId, $"question not assessed: {assessment.Outcome}"), "EvalQuestionFailed");
        }
    }

    public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) {
        logger.LogWarning("[eval {Run}] [{Index}/{Total}] {Category}/{Question} failed: {Reason}", evalRunId, index, total, category, questionId, reason);
        if (!silentPerQuestion)
            Relay(() => connection.EvalQuestionFailedAsync(evalRunId, sessionId, index, total, category, questionId, reason), "EvalQuestionFailed");
    }

    public void OnFactRetained(string category, string fact) =>
        logger.LogDebug("[eval {Run}] retained fact for {Category}: {Fact}", evalRunId, category, fact);

    public void OnRetrospectiveStarted() {
        logger.LogInformation("[eval {Run}] retrospective started", evalRunId);
        Relay(() => connection.EvalRetrospectiveStartedAsync(sessionId, evalRunId), "EvalRetrospectiveStarted");
    }

    public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective) {
        logger.LogInformation("[eval {Run}] retrospective completed", evalRunId);
        Relay(() => connection.EvalRetrospectiveCompletedAsync(sessionId, evalRunId), "EvalRetrospectiveCompleted");
    }

    public void OnRetrospectiveFailed(string reason) {
        logger.LogWarning("[eval {Run}] retrospective failed: {Reason}", evalRunId, reason);
        Relay(() => connection.EvalRetrospectiveFailedAsync(sessionId, evalRunId, reason), "EvalRetrospectiveFailed");
    }

    public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) {
        logger.LogInformation("Eval {Run} finished on session {Sid}: {Score}", evalRunId, sessionId, aggregate.OverallScore?.ToString() ?? "not scored");
        Relay(() => connection.EvalFinishedAsync(evalRunId, sessionId, aggregate.OverallScore, aggregate.Summary), "EvalFinished");
    }

    public void OnFailed(string reason) {
        logger.LogWarning("Eval {Run} failed on session {Sid}: {Reason}", evalRunId, sessionId, reason);
        Relay(() => connection.EvalFailedAsync(evalRunId, sessionId, reason), "EvalFailed");
    }

    void Relay(Func<Task> send, string eventName) {
        // Never ExecuteSynchronously: that would chain every relay onto SignalR's completion thread.
        lock (_tailLock) {
            _tail = _tail.ContinueWith(
                async _ => {
                    try {
                        await send();
                    } catch (Exception ex) {
                        logger.LogWarning(ex, "Failed to relay {Event} for eval {Run}", eventName, evalRunId);
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default
            ).Unwrap();
        }
    }
}
