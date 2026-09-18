using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Pins the protocol-1 (non-silent) <c>DaemonEvalObserver</c> relay: an unassessed
/// outcome sends exactly one signal to the server — a failure — never a null-score completion
/// alongside it. The legacy wire has no representation for "assessed but no score", and the
/// protocol-1 <c>RunQuestion</c> RPC already reports an unassessed question as a failure, so a
/// completion relay too would tell an older server the same question both completed and
/// failed.</summary>
public class EvalDaemonObserverProtocol1Tests {
    [Test]
    public async Task Unassessed_outcome_relays_a_single_failure_not_a_completion() {
        var connection = new CapturingServerConnection();
        var observer   = new DaemonEvalObserver(connection, "run-1", "sess-1", NullLogger.Instance);

        var unassessed = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "q1", Outcome = EvalOutcomes.InsufficientEvidence,
            Score    = null, Verdict = null, Finding = "evidence was truncated"
        };

        observer.OnQuestionCompleted(1, 1, unassessed, 0, 0);
        await connection.Signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(connection.CompletedCalls).IsEmpty();
        await Assert.That(connection.FailedCalls.Count).IsEqualTo(1);
        await Assert.That(connection.FailedCalls[0].Reason).Contains("insufficient_evidence");
    }

    [Test]
    public async Task Assessed_outcome_still_relays_a_completion_not_a_failure() {
        var connection = new CapturingServerConnection();
        var observer   = new DaemonEvalObserver(connection, "run-1", "sess-1", NullLogger.Instance);

        var assessed = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "q1", Outcome = EvalOutcomes.Assessed,
            Score    = 4, Verdict = "pass", Finding = "ok"
        };

        observer.OnQuestionCompleted(1, 1, assessed, 0, 0);
        await connection.Signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(connection.FailedCalls).IsEmpty();
        await Assert.That(connection.CompletedCalls.Count).IsEqualTo(1);
        await Assert.That(connection.CompletedCalls[0].Score).IsEqualTo(4);
    }

    sealed class CapturingServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance,
            TimeProvider.System) {
        public List<(string Category, string QuestionId, string Outcome, int? Score, string? Verdict)> CompletedCalls { get; } = [];
        public List<(string Category, string QuestionId, string Reason)>                               FailedCalls    { get; } = [];

        // The relay is fire-and-forget (chained onto DaemonEvalObserver's internal tail task), so
        // a test must wait for one of these rather than asserting immediately after the call.
        public TaskCompletionSource Signalled { get; } = new();

        public override Task EvalQuestionCompletedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, string outcome, int? score, string? verdict) {
            lock (CompletedCalls) CompletedCalls.Add((category, questionId, outcome, score, verdict));
            Signalled.TrySetResult();

            return Task.CompletedTask;
        }

        public override Task EvalQuestionFailedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, string reason) {
            lock (FailedCalls) FailedCalls.Add((category, questionId, reason));
            Signalled.TrySetResult();

            return Task.CompletedTask;
        }
    }
}
