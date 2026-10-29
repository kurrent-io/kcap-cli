using System.Text.Json;
using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Codex rate-limit snapshots as reading observations: a rollout <c>token_count</c> event's
/// snake_case <c>rate_limits</c>, an app-server camelCase snapshot (<c>account/rateLimits/updated</c>), and a
/// whole <c>account/rateLimits/read</c> response.</summary>
public static class CodexRateLimits {
    public const string DefaultLimitId = "codex";
    const string RateLimitReached = "rate_limit_reached";

    // DateTimeOffset.MinValue and MaxValue in Unix seconds; FromUnixTimeSeconds throws outside them.
    const long MinSeconds = -62_135_596_800;
    const long MaxSeconds = 253_402_300_799;

    sealed record Names(
        string LimitId, string UsedPercent, string WindowMinutes, string ResetsAt, string PlanType,
        string HasCredits, string ReachedType, string SpendControl);

    static readonly Names Snake = new("limit_id", "used_percent", "window_minutes", "resets_at", "plan_type", "has_credits", "rate_limit_reached_type", "spend_control_reached");
    static readonly Names Camel = new("limitId", "usedPercent", "windowDurationMins", "resetsAt", "planType", "hasCredits", "rateLimitReachedType", "spendControlReached");

    sealed record Snapshot(List<UsageWindowDto> Windows, string? Plan, CreditsDto? Credits, List<LimitSignal> Limits, bool Reached) {
        public bool IsEmpty => Windows.Count == 0 && Plan is null && Credits is null && Limits.Count == 0 && !Reached;
    }

    public static ReadingObservation? FromRollout(JsonElement rateLimits, DateTimeOffset producedAt) =>
        Observe(Read(rateLimits, Snake, null), producedAt, limitStateKnown: true);

    /// <summary>A notification's missing fields mean unavailable, not cleared, so it never clears a block.</summary>
    public static ReadingObservation? FromAppServer(JsonElement snapshot, DateTimeOffset producedAt) =>
        Observe(Read(snapshot, Camel, null), producedAt, limitStateKnown: false);

    /// <summary>One observation for the whole response, so a complete all-below answer can clear. Only
    /// <c>ordinaryUsageAllowed: true</c> makes the limit state known; <c>false</c> is a denial, which without a
    /// reached type of its own blocks whatever windows are exhausted, or blocks unknown.</summary>
    public static ReadingObservation? FromRateLimitsRead(JsonElement result, DateTimeOffset producedAt) {
        if (!result.IsObject) return null;

        var snapshots = result.Obj("rateLimitsByLimitId") is { } byId && byId.EnumerateObject().Any()
            ? byId.EnumerateObject().Select(p => Read(p.Value, Camel, p.Name)).OfType<Snapshot>().ToList()
            : Read(result.Prop("rateLimits") ?? default, Camel, null) is { } one ? [one] : [];

        var combined = new Snapshot(
            [.. snapshots.SelectMany(s => s.Windows)],
            snapshots.Select(s => s.Plan).FirstOrDefault(p => p is not null),
            snapshots.Select(s => s.Credits).FirstOrDefault(c => c is not null),
            [.. snapshots.SelectMany(s => s.Limits)],
            snapshots.Any(s => s.Reached));

        var allowed = result.Bool("ordinaryUsageAllowed");
        if (allowed == false && !combined.Reached)
            combined = combined with { Limits = [.. combined.Limits, new LimitSignal(null, null)], Reached = true };

        return Observe(combined, producedAt, limitStateKnown: allowed == true);
    }

    public static string? AccountIdFromRateLimitsRead(JsonElement result) => result.Str("accountId");

    static ReadingObservation? Observe(Snapshot? snapshot, DateTimeOffset producedAt, bool limitStateKnown) =>
        snapshot is null || snapshot.IsEmpty
            ? null
            : new ReadingObservation(producedAt, Fresh: true, snapshot.Windows, snapshot.Plan, snapshot.Credits, snapshot.Limits,
                                     snapshot.Reached, limitStateKnown);

    static Snapshot? Read(JsonElement snapshot, Names names, string? key) {
        if (!snapshot.IsObject) return null;

        var limitId = snapshot.Str(names.LimitId) ?? key ?? DefaultLimitId;
        var windows = new List<UsageWindowDto>();
        foreach (var slot in (ReadOnlySpan<string>) ["primary", "secondary"])
            if (snapshot.Obj(slot) is { } window && Window(window, names, limitId) is { } read) windows.Add(read);

        var limits      = new List<LimitSignal>();
        var reachedType = snapshot.Str(names.ReachedType);
        if (reachedType is not null)
            limits.Add(reachedType == RateLimitReached ? new LimitSignal(limitId, null) : new LimitSignal(limitId, null, Unbounded: true));

        // A spend cap is lifted by no quota reset.
        if (snapshot.Bool(names.SpendControl) == true)
            limits.Add(new LimitSignal(limitId, null, Unbounded: true));

        return new Snapshot(windows, snapshot.Str(names.PlanType), Credits(snapshot, names), limits, reachedType is not null);
    }

    static UsageWindowDto? Window(JsonElement window, Names names, string limitId) {
        if (window.Num(names.WindowMinutes) is not (> 0 and <= int.MaxValue) || window.Prop(names.UsedPercent) is not { IsNumber: true } used)
            return null;

        var             minutes  = (int) window.Num(names.WindowMinutes)!.Value;
        DateTimeOffset? resetsAt = window.Num(names.ResetsAt) is { } seconds and >= MinSeconds and <= MaxSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

        return new UsageWindowDto(limitId, minutes, used.GetDouble(), resetsAt, null);
    }

    static CreditsDto? Credits(JsonElement snapshot, Names names) {
        if (snapshot.Obj("credits") is not { } credits) return null;

        return new CreditsDto(credits.Bool(names.HasCredits) == true, credits.Bool("unlimited") == true, credits.Str("balance"));
    }
}
