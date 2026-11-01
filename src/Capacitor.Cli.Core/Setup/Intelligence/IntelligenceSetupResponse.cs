using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The answer as the server applied it.</summary>
/// <param name="BacklogSessionTouchesPerDay">How many imported sessions each kind of background work takes a day.</param>
/// <param name="Resolving">Which of <c>titles</c>, <c>outcomes</c>, <c>evals</c>, <c>correlation</c> and
/// <c>policy_judge</c> now have a model.</param>
public sealed record IntelligenceSetupResponse(
    string Mode, string? Harness, string? Vendor, bool ImportOutcomes, int BacklogSessionTouchesPerDay, IReadOnlyList<string> Resolving) {
    [JsonIgnore]
    public bool ResolvesTitles => Resolving.Contains("titles");

    /// <summary>Whole days the background work on <paramref name="sessions"/> imported sessions takes at the daily
    /// allowance, or null when the server reports no usable allowance.</summary>
    public int? DaysFor(int sessions) =>
        BacklogSessionTouchesPerDay <= 0 ? null : (int)Math.Ceiling(sessions / (double)BacklogSessionTouchesPerDay);
}
