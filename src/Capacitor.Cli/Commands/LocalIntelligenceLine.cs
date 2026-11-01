namespace Capacitor.Cli.Commands;

/// <summary>The one line <c>kcap local intelligence --json</c> prints; null members are left out.</summary>
sealed record LocalIntelligenceLine(
    bool Ok, string? Mode = null, string? Harness = null, string? Vendor = null, bool? ImportOutcomes = null,
    int? BacklogSessionTouchesPerDay = null, IReadOnlyList<string>? Resolving = null, string? Error = null);
