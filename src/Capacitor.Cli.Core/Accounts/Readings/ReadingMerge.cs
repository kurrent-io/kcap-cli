namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>The one merge for producer, daemon and server: pure, with time always passed in. Results
/// keep windows and constraints in a canonical order, so equivalent readings compare element-wise.
/// Producer times are carried through and never restamped.</summary>
public static class ReadingMerge {
    const double Exhausted = 100.0;

    public static AccountReadingDto Empty(long generation) => new(generation, [], null, null, null);

    public static AccountReadingDto Apply(AccountReadingDto held, ReadingObservation observation) {
        var stamped = observation.Windows
            .Select(w => w with { ConfirmedAt = observation.Fresh ? observation.ProducedAt : null })
            .ToArray();

        var windows = MergeWindows(held.Windows, stamped);
        var marker  = held.Blocked ?? new BlockedMarkerDto(null, null, []);

        if (observation.Limits.Count > 0)
            marker = Signal(marker, observation, stamped, held.Windows, windows);
        else if (Clears(held, marker, observation))
            marker = marker with { ClearedAt = observation.ProducedAt };

        var takeMetadata = (observation.Plan is not null || observation.Credits is not null)
                        && Compare(observation.ProducedAt, held.MetadataAt) >= 0;

        return new(
            held.Generation,
            windows,
            takeMetadata ? observation.Plan ?? held.Plan : held.Plan,
            takeMetadata ? observation.Credits ?? held.Credits : held.Credits,
            Normalize(marker.BlockedAt, marker.ClearedAt, marker.Constraints),
            takeMetadata ? observation.ProducedAt : held.MetadataAt
        );
    }

    /// <summary>Merges two snapshots of the same generation; the caller handles generation.</summary>
    public static AccountReadingDto Merge(AccountReadingDto held, AccountReadingDto incoming) {
        var a = held.Blocked;
        var b = incoming.Blocked;

        var blocked = a is null && b is null
            ? null
            : Normalize(
                Max(a?.BlockedAt, b?.BlockedAt),
                Max(a?.ClearedAt, b?.ClearedAt),
                [..a?.Constraints ?? [], ..b?.Constraints ?? []]
            );

        var (winner, loser) = Compare(incoming.MetadataAt, held.MetadataAt) >= 0 ? (incoming, held) : (held, incoming);

        return new(
            held.Generation,
            MergeWindows(held.Windows, incoming.Windows),
            winner.Plan ?? loser.Plan,
            winner.Credits ?? loser.Credits,
            blocked,
            Max(held.MetadataAt, incoming.MetadataAt)
        );
    }

    public static bool IsBlocked(AccountReadingDto reading, DateTimeOffset now) => LiveConstraints(reading, now).Count > 0;

    /// <summary>The constraints still holding at <paramref name="now"/>: none unless the marker is blocked after its
    /// last clear, otherwise every constraint whose reset is unknown or after <paramref name="now"/>.</summary>
    public static IReadOnlyList<BlockConstraintDto> LiveConstraints(AccountReadingDto reading, DateTimeOffset now) {
        if (reading.Blocked is not { BlockedAt: { } blockedAt } marker
         || blockedAt <= (marker.ClearedAt ?? DateTimeOffset.MinValue))
            return [];

        return marker.Constraints.Where(c => c.ResetsAt is not { } reset || reset > now).ToArray();
    }

    /// <summary>The latest reset among the constraints; null when any constraint is unknown or there
    /// are none. Not the next reset a screen shows, which is the earliest window reset.</summary>
    public static DateTimeOffset? BlockedUntil(AccountReadingDto reading) {
        if (reading.Blocked is not { Constraints.Length: > 0 } marker || marker.Constraints.Any(c => c.ResetsAt is null))
            return null;

        return marker.Constraints.Max(c => c.ResetsAt);
    }

    public static bool Equivalent(AccountReadingDto a, AccountReadingDto b) =>
        a.Generation == b.Generation
     && a.Plan == b.Plan
     && Equals(a.Credits, b.Credits)
     && a.MetadataAt == b.MetadataAt
     && a.Windows.SequenceEqual(b.Windows)
     && (a.Blocked, b.Blocked) switch {
            (null, null)     => true,
            ({ } x, { } y)   => x.BlockedAt == y.BlockedAt && x.ClearedAt == y.ClearedAt && x.Constraints.SequenceEqual(y.Constraints),
            _                => false
        };

    static BlockedMarkerDto Signal(
        BlockedMarkerDto                marker,
        ReadingObservation              observation,
        UsageWindowDto[]                observed,
        UsageWindowDto[]                held,
        UsageWindowDto[]                merged
    ) {
        var at = observation.ProducedAt;
        if (marker.ClearedAt is { } cleared && at <= cleared)
            return marker;

        var added = observation.Limits.SelectMany(s => Constraints(s, at, observed, held, merged));

        return new(Max(marker.BlockedAt, at), marker.ClearedAt, [..marker.Constraints, ..added]);
    }

    static IEnumerable<BlockConstraintDto> Constraints(
        LimitSignal         signal,
        DateTimeOffset      at,
        UsageWindowDto[]    observed,
        UsageWindowDto[]    held,
        UsageWindowDto[]    merged
    ) {
        var unknown = new BlockConstraintDto(signal.LimitId, signal.WindowMinutes, null, at);

        if (signal.Unbounded)
            return [unknown];

        if (signal is { LimitId: { } id, WindowMinutes: { } minutes }) {
            var window = Find(observed, id, minutes) ?? Find(held, id, minutes);
            return [unknown with { ResetsAt = window?.ResetsAt }];
        }

        var candidates = signal.LimitId is { } limitId
            ? merged.Where(w => w.LimitId == limitId).ToArray()
            : merged;

        var exhausted = candidates.Where(w => w.UsedPercent >= Exhausted).ToArray();
        if (exhausted.Length == 0 && signal.LimitId is not null && candidates.Length == 1)
            exhausted = candidates;

        return exhausted.Length == 0
            ? [unknown]
            : exhausted.Select(w => new BlockConstraintDto(w.LimitId, w.WindowMinutes, w.ResetsAt, at));
    }

    static bool Clears(AccountReadingDto held, BlockedMarkerDto marker, ReadingObservation observation) {
        var at = observation.ProducedAt;

        return observation is { Fresh: true, LimitStateKnown: true, LimitReachedReported: false, Windows.Count: > 0 }
            && at > (marker.BlockedAt ?? DateTimeOffset.MinValue)
            && at > (marker.ClearedAt ?? DateTimeOffset.MinValue)
            && observation.Windows.All(w => w.UsedPercent < Exhausted)
            && held.Windows
                   .Where(h => h.ResetsAt is not { } reset || reset > at)
                   .All(h => observation.Windows.Any(w => SameKey(w, h)));
    }

    static BlockedMarkerDto? Normalize(DateTimeOffset? blockedAt, DateTimeOffset? clearedAt, IEnumerable<BlockConstraintDto> constraints) {
        var live = constraints
            .Where(c => clearedAt is not { } cleared || c.SignaledAt > cleared)
            .Distinct()
            .OrderBy(c => c.SignaledAt)
            .ThenBy(c => c.LimitId, StringComparer.Ordinal)
            .ThenBy(c => c.WindowMinutes)
            .ThenBy(c => c.ResetsAt)
            .ToArray();

        return blockedAt is null && clearedAt is null && live.Length == 0
            ? null
            : new BlockedMarkerDto(blockedAt, clearedAt, live);
    }

    static UsageWindowDto[] MergeWindows(IEnumerable<UsageWindowDto> held, IEnumerable<UsageWindowDto> incoming) {
        var byKey = new Dictionary<(string, int), UsageWindowDto>();

        foreach (var window in held.Concat(incoming)) {
            var key = (window.LimitId, window.WindowMinutes);
            byKey[key] = byKey.TryGetValue(key, out var existing) ? MergeWindow(existing, window) : window;
        }

        return byKey.Values
            .OrderBy(w => w.LimitId, StringComparer.Ordinal)
            .ThenBy(w => w.WindowMinutes)
            .ToArray();
    }

    static UsageWindowDto MergeWindow(UsageWindowDto held, UsageWindowDto incoming) =>
        Compare(incoming.ResetsAt, held.ResetsAt) switch {
            > 0 => incoming,
            < 0 => held,
            _   => held with {
                UsedPercent = Math.Max(held.UsedPercent, incoming.UsedPercent),
                ConfirmedAt = Max(held.ConfirmedAt, incoming.ConfirmedAt)
            }
        };

    /// <summary>Null sorts first: a known time is later than an unknown one.</summary>
    static int Compare(DateTimeOffset? a, DateTimeOffset? b) => (a, b) switch {
        (null, null)       => 0,
        (null, _)          => -1,
        (_, null)          => 1,
        ({ } x, { } y)     => x.CompareTo(y)
    };

    static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => Compare(a, b) >= 0 ? a : b;

    static UsageWindowDto? Find(UsageWindowDto[] windows, string limitId, int windowMinutes) =>
        windows.FirstOrDefault(w => w.LimitId == limitId && w.WindowMinutes == windowMinutes);

    static bool SameKey(UsageWindowDto a, UsageWindowDto b) => a.LimitId == b.LimitId && a.WindowMinutes == b.WindowMinutes;
}
