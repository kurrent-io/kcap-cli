namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The daemon's own bound on each evidence-route phase: everything the phase runs, one RPC margin inside the server's
/// route-keyed deadline, so the server never times a phase out first.</summary>
public static class EvidencePhaseTimeouts {
    /// <summary>The server gives PrepareEval 60 s; the daemon stops its own evidence prepare 15 s short of that, so its
    /// failure answer still arrives in time and nothing it prepared outlives the server's interest.</summary>
    public static readonly TimeSpan DaemonPrepare = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan DaemonQuestion = EvidenceScopeClient.QuestionHeadroom(EvidenceRoute.Retrieval);
    public static readonly TimeSpan DaemonFinalize = EvidenceScopeClient.RetrospectiveHeadroom;
}
