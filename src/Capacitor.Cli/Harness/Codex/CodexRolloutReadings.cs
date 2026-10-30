using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>Records the rate limits on a Codex rollout's <c>token_count</c> lines as the owning account's
/// reading. A side output of the watcher: it reads the raw lines and never changes what is sent.</summary>
public sealed class CodexRolloutReadings(ReadingStore store, VendorAccount account) {
    const string Marker = "token_count";

    public static CodexRolloutReadings? For(AccountStore accounts, string rolloutPath, UserHome home) =>
        accounts.TryLoad() is { } registry && AccountPaths.CodexAccountForRollout(rolloutPath, registry, home) is { } account
            ? new CodexRolloutReadings(accounts.Readings, account)
            : null;

    public void Observe(string rawLine) => Observe([rawLine]);

    /// <summary>One drain batch costs one store write, however many lines it holds; observations apply in
    /// line order so a later clear still follows an earlier block. Never throws.</summary>
    public void Observe(IEnumerable<string> rawLines) {
        try {
            var observations = new List<ReadingObservation>();
            foreach (var line in rawLines)
                if (Parse(line) is { } observation) observations.Add(observation);

            if (observations.Count == 0) return;

            store.TryRecord(account.Id, (f, entry) => {
                var current = observations.Where(o => entry.GenerationStartedAt is not { } start || o.ProducedAt >= start).ToList();

                return current.Count == 0 ? null : f with { Reading = current.Aggregate(f.Reading, ReadingMerge.Apply) };
            });
        } catch (Exception) {
            // A malformed line must never reach the watcher.
        }
    }

    static ReadingObservation? Parse(string rawLine) {
        if (!rawLine.Contains(Marker, StringComparison.Ordinal)) return null;

        try {
            using var doc  = JsonDocument.Parse(rawLine);
            var       root = doc.RootElement;

            if (root.Str("type") != "event_msg" || root.Obj("payload") is not { } payload || payload.Str("type") != Marker) return null;
            if (payload.Obj("rate_limits") is not { } rateLimits) return null;
            if (!DateTimeOffset.TryParse(root.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var producedAt)) return null;

            return CodexRateLimits.FromRollout(rateLimits, producedAt);
        } catch (JsonException) {
            return null;
        }
    }
}
