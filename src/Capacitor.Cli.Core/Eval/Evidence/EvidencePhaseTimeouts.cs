namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The daemon's own bound on each evidence-route phase: everything the phase runs, one RPC margin inside the server's
/// route-keyed deadline, so the server never times a phase out first.</summary>
public static class EvidencePhaseTimeouts {
    public static readonly TimeSpan DaemonQuestion = EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.Retrieval);
    public static readonly TimeSpan DaemonFinalize = EvidenceScopeClient.RetrospectiveHeadroom;
}
