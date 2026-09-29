namespace Capacitor.Cli.Core.Eval.Evidence;

public enum EvidenceRoute { OneShot, Retrieval }

public static class EvidenceRouteExtensions {
    public const string LegacyWire                = "legacy";
    public const string LegacyTextObserverRoute   = "legacy_text";
    public const string LegacyToolsObserverRoute  = "legacy_tools";

    public static string ToWire(this EvidenceRoute route) => route switch {
        EvidenceRoute.OneShot   => "evidence_one_shot",
        EvidenceRoute.Retrieval => "evidence_retrieval",
        _                       => throw new ArgumentOutOfRangeException(nameof(route), route, null)
    };
}
