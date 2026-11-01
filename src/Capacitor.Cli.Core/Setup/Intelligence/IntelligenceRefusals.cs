namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The server's refusal codes as sentences a person can act on.</summary>
public static class IntelligenceRefusals {
    public static string Describe(string code) {
        var parts  = code.Split(':', 2);
        var detail = parts.Length > 1 ? parts[1].Trim() : "";

        return parts[0].Trim() switch {
            "not_offered"     => "This server does not run background work on a local agent.",
            "unknown_harness" => "This server does not run background work on that agent yet.",
            "unknown_vendor"  => "This server does not know that provider.",
            "key_required"    => "The provider needs an API key.",
            "model_required"  => "The provider needs a model name.",
            "key_rejected"    => $"The provider refused the key: {detail}",
            "key_pinned_above_store" => $"The server's own environment configures {detail}, which setup cannot clear.",
            "import_outcomes_pinned_above_store" => "The server's own environment decides whether imported sessions get outcomes.",
            _                 => code,
        };
    }
}
