using System.Text.Json;
using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>Merges the <c>rate_limits</c> of a Claude status line payload into an account's reading.
/// A payload is fresh only when <c>cost.total_api_duration_ms</c> exceeds the baseline recorded for its
/// session: a session's first payload may replay a response from hours ago, so it only records the baseline. A
/// counter below the baseline belongs to a restarted counter and is a first sighting too. After an account
/// switch a payload that is not fresh merges no window at all, since a session idle through the switch
/// replays the previous login's limits.</summary>
public static class ClaudeStatusLineReading {
    public const string LimitId = "claude";

    const int MaxSessions = 256;

    static readonly (string Property, int Minutes)[] Windows = [("five_hour", 300), ("seven_day", 10080)];

    /// <summary>Null when the payload carries no usable window.</summary>
    public static ReadingFile? Apply(ReadingFile held, JsonElement payload, bool afterSwitch, DateTimeOffset now) {
        if (payload.Obj("rate_limits") is not { } limits) return null;

        var windows = new List<UsageWindowDto>(Windows.Length);
        foreach (var (property, minutes) in Windows) {
            if (limits.Obj(property) is not { } window || Number(window, "used_percentage") is not { } used) continue;

            var resetsAt = Number(window, "resets_at") is { } seconds ? DateTimeOffset.FromUnixTimeMilliseconds((long) (seconds * 1000)) : (DateTimeOffset?) null;
            windows.Add(new UsageWindowDto(LimitId, minutes, used, resetsAt, null));
        }

        if (windows.Count == 0) return null;

        var sessionId = payload.Str("session_id");
        var counter   = payload.Obj("cost") is { } cost && Number(cost, "total_api_duration_ms") is { } ms ? (long) ms : (long?) null;
        var baseline  = sessionId is null ? null : held.Sessions.FirstOrDefault(s => s.SessionId == sessionId)?.ApiDurationMs;
        var fresh     = counter > baseline;

        var sessions = held.Sessions;
        if (sessionId is not null && counter is not null && counter != baseline)
            sessions = [.. held.Sessions.Where(s => s.SessionId != sessionId).TakeLast(MaxSessions - 1), new ReadingSession(sessionId, counter)];

        var observation = new ReadingObservation(now, fresh, afterSwitch && !fresh ? [] : windows, null, null, [], LimitReachedReported: false, LimitStateKnown: true);

        return held with { Reading = ReadingMerge.Apply(held.Reading, observation), Sessions = sessions };
    }

    static double? Number(JsonElement element, string property) =>
        element.Prop(property) is { IsNumber: true } value && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
