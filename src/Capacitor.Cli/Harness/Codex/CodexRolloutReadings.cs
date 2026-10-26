using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>Records the rate limits on a Codex rollout's <c>token_count</c> lines as the owning account's
/// reading. A side output of the watcher: it reads the raw lines and never changes what is sent.</summary>
public sealed class CodexRolloutReadings(ReadingStore store, VendorAccount account, string sessionId) {
    const string Marker = "token_count";

    public static CodexRolloutReadings? For(AccountStore accounts, string rolloutPath, string sessionId, UserHome home) =>
        accounts.TryLoad() is { } registry && AccountPaths.CodexAccountForRollout(rolloutPath, registry, home) is { } account
            ? new CodexRolloutReadings(accounts.Readings, account, sessionId)
            : null;

    /// <summary>Never throws: a line that is not a usable <c>token_count</c> is skipped.</summary>
    public void Observe(string rawLine) {
        if (!rawLine.Contains(Marker, StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(rawLine);
            var       root = doc.RootElement;

            if (root.Str("type") != "event_msg" || root.Obj("payload") is not { } payload || payload.Str("type") != Marker) return;
            if (payload.Obj("rate_limits") is not { } rateLimits) return;
            if (!DateTimeOffset.TryParse(root.Str("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var producedAt)) return;
            if (CodexRateLimits.FromRollout(rateLimits, producedAt) is not { } observation) return;

            store.TryRecord(account.Id, account.Generation, sessionId, f => f with { Reading = ReadingMerge.Apply(f.Reading, observation) });
        } catch (Exception) {
            // A malformed line must never reach the watcher.
        }
    }
}
