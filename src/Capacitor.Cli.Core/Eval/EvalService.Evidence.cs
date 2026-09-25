using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Eval;

public static partial class EvalService {
    public const string EvidenceScopeMovedReason = "evidence scope moved";
    public const string NoSessionMessagesReason  = "no session messages";

    const string ScopeMovedStop = "scope_moved";

    internal static readonly string[] EvidenceMcpAllowedTools = [
        $"mcp__{JudgeMcpServerName}__list_sources", $"mcp__{JudgeMcpServerName}__list_turns", $"mcp__{JudgeMcpServerName}__read_events",
        $"mcp__{JudgeMcpServerName}__read_body", $"mcp__{JudgeMcpServerName}__list_calls", $"mcp__{JudgeMcpServerName}__summarize_calls",
        $"mcp__{JudgeMcpServerName}__list_authorizations", $"mcp__{JudgeMcpServerName}__open_page"
    ];

    // The legacy verdict schema plus an optional citations array; `required` is unchanged so a reply without citations stays valid.
    internal const string EvidenceVerdictJsonSchema = """
        {"type":"object","properties":{"category":{"type":"string"},"question_id":{"type":"string"},"outcome":{"type":"string","enum":["assessed","insufficient_evidence","not_applicable"]},"score":{"type":["integer","null"],"minimum":1,"maximum":5},"verdict":{"type":["string","null"],"enum":["pass","warn","fail",null]},"finding":{"type":"string","minLength":1},"evidence":{"type":["string","null"]},"recommendation":{"type":["string","null"]},"retain_fact":{"type":["string","object","null"],"properties":{"fact":{"type":"string"},"applies_to_vendors":{"type":"array","items":{"type":"string"},"maxItems":16},"applies_to_session_kinds":{"type":"array","items":{"type":"string"},"maxItems":16}},"required":["fact"],"additionalProperties":false},"citations":{"type":"array","items":{"type":"string"},"maxItems":200}},"required":["category","question_id","outcome","score","verdict","finding","evidence","recommendation","retain_fact"],"additionalProperties":false}
        """;

    // The reporting question's contract: the same schema with the optional obligations array beside citations.
    internal static readonly string EvidenceReportingVerdictJsonSchema =
        EvidenceVerdictJsonSchema.Replace(",\"citations\":", ",\"obligations\":" + EvalObligationContract.ObligationsJsonSchema + ",\"citations\":", StringComparison.Ordinal);

    static async Task<SessionEvalCompletedPayloadV4?> RunEvidenceAsync(
            string baseUrl, HttpClient httpClient, Profile? profile, HarnessRegistry harnesses, string sessionId, IReadOnlyList<EvalQuestionDto> questions,
            EvalCatalogDto catalog, string model, IEvalObserver observer, TimeProvider time, CancellationToken ct, string? evalRunId) {
        var setup = await PrepareEvidenceAsync(baseUrl, httpClient, profile, harnesses, sessionId, questions, catalog, model, observer, time, ct, evalRunId);
        if (setup is null) return null;

        try {
            var assessments = new List<EvalQuestionAssessment>();
            var failures    = new List<EvalQuestionFailure>();
            for (var i = 0; i < setup.Questions.Count; i++) {
                var outcome = await RunEvidenceQuestionAsync(setup, httpClient, baseUrl, setup.Questions[i], model, i + 1, setup.Questions.Count, observer, time, ct);
                if (outcome.ScopeMoved) return ScopeMoved(setup, observer);
                if (outcome.Assessment is { } assessment) assessments.Add(assessment);
                else if (outcome.Failure is { } failure) failures.Add(failure);
            }
            return await FinalizeEvidenceAsync(setup, httpClient, baseUrl, assessments, failures, model, observer, time, ct);
        } finally {
            await DisposeSetupAsync(setup, observer);
        }
    }

    public static async Task<EvidenceRunSetup?> PrepareEvidenceAsync(
            string baseUrl, HttpClient httpClient, Profile? profile, HarnessRegistry harnesses, string sessionId, IReadOnlyList<EvalQuestionDto> questions,
            EvalCatalogDto catalog, string model, IEvalObserver observer, TimeProvider time, CancellationToken ct,
            string? evalRunId = null, string? tempRoot = null) {
        var ad = catalog.EvidenceRetrieval ?? throw new ArgumentException("the catalog does not advertise the evidence route", nameof(catalog));
        evalRunId ??= Guid.NewGuid().ToString();

        IReadOnlyList<EvalQuestionDto> reconciled;
        try {
            reconciled = ReconcileQuestions([.. questions.Select(q => q.Id)], catalog);
        } catch (ArgumentException ex) {
            observer.OnFailed($"eval catalog reconciliation failed: {ex.Message}");
            return null;
        }

        var root = tempRoot ?? Path.GetTempPath();
        EvidenceRunContext.SweepStale(root, time, observer.OnInfo);
        var context  = EvidenceRunContext.Create(evalRunId, root);
        var prepared = false;
        try {
            var scope  = new EvidenceScopeClient(httpClient, baseUrl, sessionId, time);
            var status = await scope.ResolveAsync(ct);
            if (status != EvidenceScopeStatus.Ok) {
                observer.OnFailed(status switch {
                    EvidenceScopeStatus.NotVisible => "session not found or not visible",
                    EvidenceScopeStatus.Moved      => EvidenceScopeMovedReason,
                    _                              => scope.LastError ?? "failed to resolve the evidence scope"
                });
                return null;
            }

            var state = scope.State!;
            if (!state.Sources.Any(s => s.Kind == "root" && s.IsAvailable) || !state.Sources.Any(s => s.IsAvailable && s.RevisionCutoff >= s.FirstRevision)) {
                observer.OnFailed(NoSessionMessagesReason);
                return null;
            }

            var reader = new EvidenceReadClient(httpClient, baseUrl, sessionId);
            var limit  = EvidenceRouteChoice.OneShotLimitChars(ad.OneShotLimitChars, TraceTokenBudget());
            var (route, trace) = await EvidenceRouteChoice.ChooseAsync(new EvidenceTraceAssembler(reader, state.Token, ad.PageBudgetBytes), state.Sources, limit, ct);
            if (trace.FailedStatus is { } failed && !await ContinuesAfterReadFailureAsync(failed, scope, observer, ct)) return null;

            var orientation = await new EvidenceOrientationBuilder(reader).BuildAsync(state, EvidenceBudgets.OrientationBytes, ad.PageBudgetBytes, ct);
            if (orientation.FailedStatus is not null) {
                observer.OnFailed(EvidenceScopeMovedReason);
                return null;
            }

            observer.OnStarted(evalRunId, model, reconciled.Count);
            observer.OnContextFetched(trace.Fits ? trace.Cites.Count : 0, trace.Fits ? trace.Chars : 0, 0, 0, 0);

            prepared = true;
            return new EvidenceRunSetup {
                Context = context, Scope = scope, Reader = reader, Citations = new EvidenceCitationClient(httpClient, baseUrl, sessionId, time),
                Advertisement = ad, Route = route, Trace = trace, OneShotLimitChars = limit, Orientation = orientation, Questions = reconciled,
                ReportingQuestionId = EvalStrategiesMirror.ReportingQuestion(catalog, [.. reconciled.Select(q => q.Id)]),
                EvalRunId = evalRunId, SessionId = sessionId, EncodedSessionId = Uri.EscapeDataString(sessionId),
                RetrospectivePrompt = catalog.RetrospectivePrompt, RetrospectivePromptVersion = catalog.RetrospectivePromptVersion,
                Model = model, Profile = profile, Harnesses = harnesses
            };
        } finally {
            if (!prepared) {
                try { await context.DisposeAsync(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { observer.OnInfo($"could not remove {context.RunDirectory}: {e.Message}"); }
            }
        }
    }

    // A read that failed while the route was being chosen: a 409, or a 404 the cursor re-open confirms, means the scope is
    // gone; a refused source leaves retrieval to report it; anything else only means the trace cannot be assembled whole.
    static async Task<bool> ContinuesAfterReadFailureAsync(int status, EvidenceScopeClient scope, IEvalObserver observer, CancellationToken ct) {
        if (status == 404) {
            if (await scope.ReopenAsync(ct) == EvidenceScopeStatus.Ok) return true;
            observer.OnFailed(EvidenceScopeMovedReason);
            return false;
        }
        switch (status) {
            case 409:
                observer.OnFailed(EvidenceScopeMovedReason);
                return false;
            case 401:
                observer.OnFailed("authentication failed — run 'kcap login' to re-authenticate");
                return false;
            case 0:
                observer.OnFailed("server unreachable while preparing the evidence route");
                return false;
            default:
                return true;
        }
    }

    public static async Task<EvidenceQuestionOutcome> RunEvidenceQuestionAsync(
            EvidenceRunSetup setup, HttpClient httpClient, string baseUrl, EvalQuestionDto question, string model,
            int index, int total, IEvalObserver observer, TimeProvider time, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        observer.OnQuestionStarted(index, total, question.Category, question.Id);

        EvalUsage? usage       = null;
        var        diagnostics = new List<string>();
        var        route       = setup.Route;

        var renewed = await setup.Scope.EnsureScopeAsync(EvidenceScopeClient.QuestionHeadroom(route), ct);
        if (renewed == EvidenceScopeStatus.Moved) return EvidenceQuestionOutcome.Moved;
        if (renewed != EvidenceScopeStatus.Ok) return Failed(Failure(EvalFailureCodes.ChatError), setup.Scope.LastError ?? "could not renew the evidence scope");

        var state      = setup.Scope.State!;
        var ledgerPath = setup.Context.LedgerFilePath(index);
        var maxTurns   = route == EvidenceRoute.Retrieval ? setup.MaxTurns : JudgeMaxTurns;
        var started    = time.GetTimestamp();
        void Log(string msg) { diagnostics.Add(msg); observer.OnInfo($"  {msg}"); }

        EvidenceFirstView? view = null;
        var reporting = false;
        ClaudeCliOutcome outcome;
        if (route == EvidenceRoute.OneShot) {
            WriteOneShotLedger(setup, index, question, state, time);
            outcome = await ClaudeCliRunner.RunDetailedAsync(
                BuildOneShotPrompt(question, state.RootSessionId, setup.EvalRunId, setup.Trace.TraceJson), setup.OneShotTimeout, time, Log, setup.Profile, setup.Harnesses,
                model: JudgeModelFor(model), maxTurns: JudgeMaxTurns, promptViaStdin: true, jsonSchema: EvidenceVerdictJsonSchema, ct: ct);
        } else {
            if (question.Strategy is { Length: > 0 } strategy && !setup.FirstViews.TryGetValue(strategy, out view)) {
                var (built, failedView, answered) = await EvidenceFirstViewReader.ReadAsync(setup.Reader, state.Token, strategy, setup.Orientation.Pages.Count, ct);
                if (failedView is not null) return EvidenceQuestionOutcome.Moved;
                view = built;
                if (answered) setup.FirstViews[strategy] = built;
            }
            // As on the server's own route, only a question that received its first view reports obligations.
            reporting = view is not null && question.Id == setup.ReportingQuestionId;
            IReadOnlyList<JudgeLedgerPage> seeded = view is null ? setup.Orientation.Pages : [.. setup.Orientation.Pages, .. view.Pages];

            var soft = time.GetUtcNow() + setup.RetrievalTimeout * EvidenceBudgets.SoftDeadlineFraction;
            using (var writer = JudgeLedgerWriter.Create(ledgerPath, new JudgeLedgerHeader(setup.EvalRunId, question.Id, state.ScopeVersion, setup.Budgets, soft, time.GetUtcNow())))
                foreach (var page in seeded) writer.Append(page);
            setup.Context.WriteRunFile(index, new EvidenceRunFile(setup.EvalRunId, question.Id, state.RootSessionId, state.ScopeVersion, state.Token, state.Deadline,
                [.. state.Sources.Select(EvidenceRunSource.From)], setup.Budgets, soft, ledgerPath, seeded));
            try {
                var prompt = BuildEvidenceQuestionPrompt(question, state.RootSessionId, setup.EvalRunId, setup.Orientation.Text + view?.Text, setup.Advertisement.MaxToolCalls)
                           + (reporting ? "\n" + EvalObligationContract.ReporterMarker : "");
                outcome = await ClaudeCliRunner.RunDetailedAsync(prompt,
                    setup.RetrievalTimeout, time, Log, setup.Profile, setup.Harnesses,
                    model: JudgeModelFor(model), maxTurns: maxTurns, promptViaStdin: true, jsonSchema: reporting ? EvidenceReportingVerdictJsonSchema : EvidenceVerdictJsonSchema,
                    mcpConfigJson: BuildEvidenceMcpConfig(ResolveJudgeCommandPath(), setup.SessionId, setup.Context.RunFilePath(index), baseUrl),
                    allowedTools: EvidenceMcpAllowedTools, maxBudgetUsd: ToolsPerQuestionMaxBudgetUsd, ct: ct);
            } finally {
                setup.Context.DeleteRunFile(index);
            }
        }

        var ledger = JudgeLedgerReader.Read(ledgerPath);
        if (ledger.StopReason == ScopeMovedStop) return EvidenceQuestionOutcome.Moved;
        usage = outcome.Result is { } finished ? EvalUsage.FromResult(finished) : null;
        var budgetStop = ledger.StopReason is { } stop && EvalStopReasons.All.Contains(stop) ? stop : null;

        if (outcome.Failure is not null && EvidenceFailureCode(outcome) is var code && code != EvalFailureCodes.VerdictParseFailed) {
            var failure = code == EvalFailureCodes.IterationCap
                ? EvidenceCoverageMeasure.IterationCap(question.Category, question.Id, state, ledger, maxTurns)
                : Failure(code);
            return Failed(failure, diagnostics.Count == 0 ? $"claude {code}" : $"claude {code}; {string.Join(" | ", diagnostics.Select(d => Truncate(d, 300)))}");
        }

        var raw        = outcome.Result?.Result;
        var assessment = raw is null ? null : ParseVerdict(raw, question, msg => observer.OnInfo($"  {question.Category}/{question.Id}: {msg}"));
        var tokens     = raw is null ? [] : ParseCitationTokens(raw);
        var synthesized = false;
        if (assessment is null) {
            if (budgetStop is null)
                return Failed(Failure(EvalFailureCodes.VerdictParseFailed), raw is null ? "claude verdict_parse_failed" : $"verdict JSON could not be parsed; raw response: {Truncate(raw, 500)}");
            assessment = new EvalQuestionAssessment {
                Category = question.Category, QuestionId = question.Id, Outcome = EvalOutcomes.InsufficientEvidence,
                Finding  = $"Retrieval stopped on {budgetStop} and the judge returned no usable verdict."
            };
            tokens      = [];
            synthesized = true;
        }

        IReadOnlyList<EvalReportedObligation>? reported = null;
        string? obligationsLoss = null;
        if (reporting) {
            if (synthesized) obligationsLoss = ObligationLossBudgetStop;
            else if (ObligationsMember(raw!) is { } member) {
                using (member) reported = EvalObligationContract.TryParse(member.RootElement.GetProperty("obligations"));
                if (reported is null) obligationsLoss = ObligationLossUnparseable;
            }
        }

        // Verdict citations first, then every obligation handle, deduplicated by ref, so one certification slice covers both.
        // No wire field carries these counts, so every citation that does not reach the payload is reported here.
        var verdictRefs = JudgeCiteHandles.Expand(tokens, ledger, EvidenceBudgets.MaxCitations, out var unexpanded);
        var handleRefs  = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in reported?.SelectMany(o => o.Citations.Prepend(o.Anchor)) ?? [])
            if (ledger.Cites.TryGetValue(handle, out var expanded)) handleRefs[handle] = expanded;
        var refs      = verdictRefs.Concat(handleRefs.Values).Distinct(StringComparer.Ordinal).Take(EvidenceBudgets.MaxCitations).ToList();
        var certified = new List<EvalEvidenceCitation>();
        if (unexpanded > 0) Note($"{unexpanded} citation(s) dropped: unknown handle or over the {EvidenceBudgets.MaxCitations}-citation cap");
        if (refs.Count > 0) {
            var slice = await setup.Scope.EnsureScopeAsync(EvidenceScopeClient.CertificationHeadroom, ct);
            if (slice == EvidenceScopeStatus.Moved) return EvidenceQuestionOutcome.Moved;
            if (slice == EvidenceScopeStatus.Ok) {
                var certification = await setup.Citations.CertifyAsync(setup.Scope.State!.Token, setup.Scope.State.ScopeVersion, refs, ct);
                if (certification.ScopeLost) return EvidenceQuestionOutcome.Moved;
                certified.AddRange(certification.Certified);
                if (certification.Dropped > 0) Note($"{certification.Dropped} of {refs.Count} citation(s) not certified");
            } else {
                Note($"certification skipped ({slice}): {setup.Scope.LastError ?? "could not renew the evidence scope"}; {refs.Count} citation(s) not certified");
            }
        }

        var byRef    = certified.DistinctBy(c => c.Ref, StringComparer.Ordinal).ToDictionary(c => c.Ref, StringComparer.Ordinal);
        var byHandle = handleRefs.Where(h => byRef.ContainsKey(h.Value)).ToDictionary(h => h.Key, h => byRef[h.Value], StringComparer.Ordinal);
        assessment = EvalObligationRules.Reconcile(assessment, reported, byHandle, reporting);
        if (reported is { Count: > 0 } && assessment.Obligations is null) obligationsLoss ??= ObligationLossUncertified;
        var cited = certified.Where(c => verdictRefs.Contains(c.Ref, StringComparer.Ordinal))
            .Concat(assessment.Obligations?.SelectMany(o => o.Citations.Prepend(o.Anchor)) ?? [])
            .DistinctBy(c => c.Ref, StringComparer.Ordinal).Take(EvidenceBudgets.MaxCitations).ToList();

        var bound    = setup.Scope.State!;
        var coverage = route == EvidenceRoute.Retrieval ? EvidenceCoverageMeasure.ForRetrieval(bound, ledger, cited) : EvidenceCoverageMeasure.ForOneShot(bound, cited);
        var trace    = route == EvidenceRoute.Retrieval
            ? EvidenceCoverageMeasure.RetrievalTraceCoverage(ledger, outcome.Result?.NumTurns ?? 0, maxTurns)
            : EvalTraceCoverage.ForOneShot(false, setup.Trace.Chars, setup.Trace.TotalChars, setup.OneShotLimitChars);
        assessment = assessment with {
            EvidenceCoverage = ReconcileEvidenceCoverage(assessment.Outcome, coverage),
            TraceCoverage    = trace,
            ToolsUsed        = route == EvidenceRoute.Retrieval ? ledger.ToolCalls : null,
            Strategy         = view?.Strategy,
            StrategyVersion  = view?.StrategyVersion,
            ObligationsNotReported = assessment.Obligations is null ? obligationsLoss : null
        };
        if (assessment.Validate() is { } invalid) return Failed(Failure(EvalFailureCodes.ChatError), $"assessment rejected: {invalid}");

        observer.OnQuestionCompleted(index, total, assessment, usage ?? new EvalUsage(), route.ToWire(), time.GetElapsedTime(started), runnerInvocations: 1);
        observer.OnQuestionLedger(index, question.Id, ledgerPath);
        if (raw is not null && ExtractRetainFact(raw) is { } fact) setup.Context.BufferRetainedFact(question.Category, fact);
        return new EvidenceQuestionOutcome(assessment, null, usage, false);

        void Note(string msg) => observer.OnInfo($"  {question.Category}/{question.Id}: {msg}");

        EvalQuestionFailure Failure(string failureCode) => new() { Category = question.Category, QuestionId = question.Id, Code = failureCode };

        EvidenceQuestionOutcome Failed(EvalQuestionFailure failure, string reason) {
            observer.OnQuestionFailed(index, total, question.Category, question.Id, reason);
            return new(null, failure, usage, false);
        }
    }

    public static async Task<SessionEvalCompletedPayloadV4?> FinalizeEvidenceAsync(
            EvidenceRunSetup setup, HttpClient httpClient, string baseUrl, IReadOnlyList<EvalQuestionAssessment> assessments,
            IReadOnlyList<EvalQuestionFailure> failures, string model, IEvalObserver observer, TimeProvider time, CancellationToken ct) {
        if (assessments.Count == 0) {
            setup.Context.DiscardRetainedFacts();
            observer.OnFailed("all judge invocations failed");
            return null;
        }

        var aggregate = Aggregate(assessments, failures, setup.EvalRunId, model, setup.Questions) with {
            FactsUsed = [], CoveragePolicyVersion = EvidenceCoveragePolicyVersion, EvidenceScopeVersion = setup.Scope.State!.ScopeVersion
        };

        EvalRetrospectiveV2? retrospective = null;
        var clean = true;
        if (assessments.Any(a => a.Outcome == EvalOutcomes.Assessed)) {
            var renewed = await setup.Scope.EnsureScopeAsync(EvidenceScopeClient.RetrospectiveHeadroom, ct);
            if (renewed == EvidenceScopeStatus.Moved) return ScopeMoved(setup, observer);
            if (renewed == EvidenceScopeStatus.Ok) {
                var (trace, failed) = await new EvidenceRetrospectiveInputs(setup.Reader)
                    .BuildTraceAsync(setup.Scope.State!, assessments, setup.Advertisement.RetrospectiveEvidenceBytes, ObligationCitations(assessments), ct);
                if (failed is not null) return ScopeMoved(setup, observer);
                retrospective = await RunEvidenceRetrospectiveAsync(setup, model, aggregate, assessments, trace, observer, time, ct);
            } else {
                observer.OnRetrospectiveFailed(setup.Scope.LastError ?? "could not renew the evidence scope");
            }
            clean = retrospective is not null;
        }
        aggregate = aggregate with { Retrospective = retrospective, RetrospectivePromptVersion = setup.RetrospectivePromptVersion };

        // A best-effort abort before anything is written; the eval-read floor covers a revocation after this check.
        var admitted = await setup.Scope.EnsureScopeAsync(EvidenceScopeClient.PreDrainHeadroom, ct);
        if (admitted == EvidenceScopeStatus.Moved) return ScopeMoved(setup, observer);
        if (admitted != EvidenceScopeStatus.Ok) {
            observer.OnInfo($"retained facts discarded: the pre-drain admission check failed ({setup.Scope.LastError ?? admitted.ToString()})");
            clean = false;
        }
        if (clean)
            await setup.Context.DrainRetainedFactsAsync((category, fact, token) => PostJudgeFactAsync(httpClient, baseUrl, setup.EncodedSessionId, category, fact.Fact,
                setup.EvalRunId, fact.AppliesToVendors, fact.AppliesToSessionKinds, observer, time, token), observer, ct);
        else
            setup.Context.DiscardRetainedFacts();

        if (!await PersistAggregateV4Async(httpClient, baseUrl, setup.EncodedSessionId, aggregate, observer, time, ct)) return null;
        observer.OnFinished(aggregate);
        return aggregate;
    }

    /// <summary>Deletes the run directory, logging rather than throwing when it cannot be removed.</summary>
    public static async Task DisposeSetupAsync(EvidenceRunSetup setup, IEvalObserver observer) {
        try { await setup.DisposeAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { observer.OnInfo($"could not remove {setup.Context.RunDirectory}: {e.Message}"); }
    }

    static SessionEvalCompletedPayloadV4? ScopeMoved(EvidenceRunSetup setup, IEvalObserver observer) {
        setup.Context.DiscardRetainedFacts();
        observer.OnFailed(EvidenceScopeMovedReason);
        return null;
    }

    static async Task<EvalRetrospectiveV2?> RunEvidenceRetrospectiveAsync(
            EvidenceRunSetup setup, string model, SessionEvalCompletedPayloadV4 aggregate, IReadOnlyList<EvalQuestionAssessment> assessments,
            string trace, IEvalObserver observer, TimeProvider time, CancellationToken ct) {
        observer.OnRetrospectiveStarted();
        var started      = time.GetTimestamp();
        var overallText  = aggregate.OverallScore is { } score ? $"{score}/5" : "not scored";
        var sessionMeta  = $"session-id: {setup.Scope.State!.RootSessionId}\nrun-id: {setup.EvalRunId}\nmodel: {model}\noverall-score: {overallText}";
        var verdictsJson = RetrospectiveVerdictsJson(assessments);
        var prompt       = EvidencePromptBlocks.Preamble(EvidencePromptBlocks.RetrospectivePreambleResource)
                         + BuildRetrospectivePrompt(setup.RetrospectivePrompt, sessionMeta, verdictsJson, knownPatterns: "", trace);
        try {
            var result = await ClaudeCliRunner.RunAsync(prompt, setup.RetrospectiveTimeout, time, msg => observer.OnInfo($"  {msg}"), setup.Profile, setup.Harnesses,
                model: JudgeModelFor(model), maxTurns: JudgeMaxTurns, promptViaStdin: true, jsonSchema: RetrospectiveJsonSchema, ct: ct);
            if (result is null) {
                observer.OnRetrospectiveFailed("claude returned null (timeout, non-zero exit, or unparseable response)");
                return null;
            }
            var retrospective = ParseRetrospectiveV2(result.Result);
            if (retrospective is null) {
                observer.OnRetrospectiveFailed($"retrospective response did not parse as expected JSON shape; raw response: {Truncate(result.Result, 500)}");
                return null;
            }
            observer.OnRetrospectiveCompleted(retrospective, EvalUsage.FromResult(result), time.GetElapsedTime(started));
            return retrospective;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            observer.OnRetrospectiveFailed(ex.Message);
            return null;
        }
    }

    // Written before the harness runs: the assembled trace as page "e" with the entries it delivered and the outline's
    // turns, then a footer.
    static void WriteOneShotLedger(EvidenceRunSetup setup, int index, EvalQuestionDto question, EvidenceScopeState state, TimeProvider time) {
        var trace = setup.Trace;
        var page  = new JudgeLedgerPage(0, "e", "one_shot", "{}", null, trace.TraceJson,
            Runs(trace.Detail.Select(d => (d.Source, d.Revision))), setup.Orientation.OutlineTurns, [], trace.Detail, trace.Cites, false, null);
        using var writer = JudgeLedgerWriter.Create(setup.Context.LedgerFilePath(index),
            new JudgeLedgerHeader(setup.EvalRunId, question.Id, state.ScopeVersion, setup.Budgets, null, time.GetUtcNow()));
        writer.Append(page);
        writer.Append(new JudgeLedgerFooter(0, page.Bytes, null, [], time.GetUtcNow()));
    }

    static List<(string Source, long From, long To)> Runs(IEnumerable<(string Source, long Revision)> events) {
        var runs = new List<(string Source, long From, long To)>();
        foreach (var (source, revision) in events) {
            if (runs.Count > 0 && runs[^1].Source == source && runs[^1].To + 1 == revision) runs[^1] = (source, runs[^1].From, revision);
            else runs.Add((source, revision, revision));
        }
        return runs;
    }

    internal static string BuildOneShotPrompt(EvalQuestionDto question, string sessionId, string evalRunId, string traceJson) =>
        EvidencePromptBlocks.Preamble(EvidencePromptBlocks.OneShotPreambleResource) + EvidencePromptBlocks.Render(question.Prompt, new Dictionary<string, string>(StringComparer.Ordinal) {
            ["{CACHE_BOUNDARY}"] = "", ["{SESSION_ID}"] = sessionId, ["{EVAL_RUN_ID}"] = evalRunId, ["{CATEGORY}"] = question.Category,
            ["{QUESTION_ID}"] = question.Id, ["{KNOWN_PATTERNS}"] = "", ["{TASKS}"] = EvidencePromptBlocks.TasksReadFromEvidence, ["{TRACE_JSON}"] = traceJson
        });

    internal static string BuildEvidenceQuestionPrompt(EvalQuestionDto question, string sessionId, string evalRunId, string orientation, int maxToolCalls) =>
        EvidencePromptBlocks.Render(EmbeddedResources.Load(EvidencePromptBlocks.QuestionTemplateResource), new Dictionary<string, string>(StringComparer.Ordinal) {
            ["{SESSION_ID}"] = sessionId, ["{EVAL_RUN_ID}"] = evalRunId, ["{CATEGORY}"] = question.Category, ["{QUESTION_ID}"] = question.Id,
            ["{QUESTION_TEXT}"] = question.RawText ?? question.Prompt, ["{TASKS}"] = EvidencePromptBlocks.TasksReadFromEvidence,
            ["{ORIENTATION}"] = orientation, ["{MAX_TOOL_CALLS}"] = maxToolCalls.ToString(CultureInfo.InvariantCulture)
        });

    // The MCP process reads the token and manifest from the owner-only run file; only its path is passed.
    internal static string BuildEvidenceMcpConfig(string commandPath, string sessionId, string runPath, string baseUrl) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            w.WriteStartObject("mcpServers");
            w.WriteStartObject(JudgeMcpServerName);
            w.WriteString("command", commandPath);
            w.WriteStartArray("args");
            foreach (var arg in new[] { "mcp", "judge", "--session", sessionId, "--run", runPath }) w.WriteStringValue(arg);
            w.WriteEndArray();
            w.WriteStartObject("env");
            w.WriteString(ProfileOverrides.UrlVar, baseUrl);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The retrospective reads obligation citations through the coverage record, so each obligation travels as its id and
    // status only: titles and notes are judge-written free text.
    internal static string RetrospectiveVerdictsJson(IReadOnlyList<EvalQuestionAssessment> assessments) {
        if (assessments.All(a => a.Obligations is null)) return JsonSerializer.Serialize(assessments.ToList(), CapacitorJsonContext.Default.ListEvalQuestionAssessment);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartArray();
            foreach (var a in assessments) {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(a with { Obligations = null }, CapacitorJsonContext.Default.EvalQuestionAssessment));
                w.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject()) property.WriteTo(w);
                if (a.Obligations is { Count: > 0 } obligations) {
                    w.WriteStartArray("obligations");
                    foreach (var o in obligations) {
                        w.WriteStartObject();
                        w.WriteString("id", o.Id);
                        w.WriteString("status", o.Status);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    const string ObligationLossUnparseable = "unparseable";
    const string ObligationLossBudgetStop  = "budget_stop";
    const string ObligationLossUncertified = "uncertified";

    // The reply parsed once more, when it carries a non-null obligations member.
    static JsonDocument? ObligationsMember(string raw) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(StripCodeFences(raw.Trim())); }
        catch (JsonException) { return null; }
        if (doc.RootElement.Prop("obligations") is { IsNull: false }) return doc;
        doc.Dispose();
        return null;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<EvalEvidenceCitation>> ObligationCitations(IReadOnlyList<EvalQuestionAssessment> assessments) =>
        assessments.Where(a => a.Obligations is { Count: > 0 }).ToDictionary(a => a.QuestionId,
            a => (IReadOnlyList<EvalEvidenceCitation>)[.. a.Obligations!.SelectMany(o => o.Citations.Prepend(o.Anchor))], StringComparer.Ordinal);

    internal static IReadOnlyList<string> ParseCitationTokens(string rawResponse) {
        try {
            using var doc = JsonDocument.Parse(StripCodeFences(rawResponse.Trim()));
            return doc.RootElement.Arr("citations") is { } citations
                ? [.. citations.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!)]
                : [];
        } catch (JsonException) {
            return [];
        }
    }
}
