using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One evidence-route run as prepared: its private directory, the bound scope and its clients, the route, the fitting
/// one-shot trace (empty on retrieval), the orientation and the reconciled questions. Disposing it deletes the directory.</summary>
public sealed class EvidenceRunSetup : IAsyncDisposable {
    public required EvidenceRunContext             Context                    { get; init; }
    public required EvidenceScopeClient            Scope                      { get; init; }
    public required EvidenceReadClient             Reader                     { get; init; }
    public required EvidenceCitationClient         Citations                  { get; init; }
    public required EvalEvidenceAdvertisementDto   Advertisement              { get; init; }
    public required EvidenceRoute                  Route                      { get; init; }
    public required EvidenceTraceResult            Trace                      { get; init; }
    public required int                            OneShotLimitChars          { get; init; }
    public required EvidenceOrientation            Orientation                { get; init; }
    public required IReadOnlyList<EvalQuestionDto> Questions                  { get; init; }
    public required string                         EvalRunId                  { get; init; }
    public required string                         SessionId                  { get; init; }
    public required string                         EncodedSessionId           { get; init; }
    public required string                         RetrospectivePrompt        { get; init; }
    public required string                         RetrospectivePromptVersion { get; init; }
    public required string                         Model                      { get; init; }
    public required Profile?                       Profile                    { get; init; }
    public required HarnessRegistry                Harnesses                  { get; init; }

    // The harness budget per phase; settable so tests can shorten it.
    internal TimeSpan OneShotTimeout       { get; set; } = EvalService.OneShotQuestionTimeout;
    internal TimeSpan RetrievalTimeout     { get; set; } = EvalService.ToolsPerQuestionTimeout;
    internal TimeSpan RetrospectiveTimeout { get; set; } = EvalService.RetrospectiveTimeout;

    public EvidenceRunBudgets Budgets  => new(Advertisement.MaxToolCalls, Advertisement.JudgeByteBudgetBytes, Advertisement.PageBudgetBytes);
    public int                MaxTurns => EvidenceBudgets.MaxTurns(Advertisement.MaxToolCalls);

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}
