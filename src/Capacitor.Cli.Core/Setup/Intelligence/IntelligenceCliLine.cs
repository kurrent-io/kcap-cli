namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The one line <c>kcap local intelligence --json</c> prints, which the desktop app reads; null members are
/// left out.</summary>
public sealed record IntelligenceCliLine(
    bool Ok, string? Mode = null, string? Harness = null, string? Vendor = null, bool? ImportOutcomes = null,
    int? BacklogSessionTouchesPerDay = null, IReadOnlyList<string>? Resolving = null, string? Error = null) {
    public static IntelligenceCliLine From(IntelligenceSetupResponse r) =>
        new(true, r.Mode, r.Harness, r.Vendor, r.ImportOutcomes, r.BacklogSessionTouchesPerDay, r.Resolving);

    /// <summary>The applied answer, or null for a refusal or a line missing what an applied one carries.</summary>
    public IntelligenceSetupResponse? Applied =>
        Ok && Mode is not null && ImportOutcomes is { } outcomes && BacklogSessionTouchesPerDay is { } perDay && Resolving is not null
            ? new(Mode, Harness, Vendor, outcomes, perDay, Resolving)
            : null;
}
