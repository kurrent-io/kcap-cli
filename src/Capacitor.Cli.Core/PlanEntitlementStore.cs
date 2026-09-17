using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core;

/// <summary>
/// Durable, per-server cache of the tenant's plan entitlements, learned passively from the
/// <c>X-Kcap-Plan</c> response header (see <c>PlanEntitlementCaptureHandler</c>). The SessionStart
/// nudges read it so an agent is never told to use a tool its tenant's plan refuses: without it a Free
/// tenant is nudged toward <c>declare_work_item</c> every session and every call bounces 403.
///
/// <para>One flat file per normalized server URL under the caller's <see cref="ConfigRoot"/>, the
/// <see cref="ServerVersionStore"/> shape: a multi-profile user gets per-server entitlements for free
/// and two servers never race one file. Deliberately NOT per-session state — every harness on the
/// machine shares this one answer, so a plan change costs at most one stale nudge rather than one per
/// harness.</para>
///
/// <para>Best-effort throughout, and fail-open in both directions: an unreadable, absent or stale
/// file reads as <see cref="PlanEntitlements.Unknown"/>, which nudges exactly as today.</para>
/// </summary>
public static class PlanEntitlementStore {
    /// <summary>How long a cached answer outlives its last sighting. Never reached while the server
    /// sends the header — every authenticated response refreshes it — so this bounds one case only:
    /// a server rolled back to a build that does not send it, where a denial would otherwise suppress
    /// a paying tenant's nudges permanently with no way back.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>How long the in-process memo suppresses an identical rewrite. Bounded well below
    /// <see cref="StaleAfter"/> deliberately: a long-lived process (the daemon, an MCP server) that
    /// keeps observing the same denial must still refresh <c>seen_at</c>, or its own write dedupe
    /// would age out the very record it is confirming.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);

    // Per-process memo, so a long-lived process making many requests doesn't rewrite the same file on
    // every response — bounded by RefreshAfter so suppression can never outlive the freshness horizon.
    //
    // It records what we wrote, which is NOT the same as what the file holds: the cache is shared by
    // independent kcap processes, so a peer can overwrite it between our writes. Suppression therefore
    // re-reads before trusting the memo (see ShouldSkip) — a small read on the hot path in place of a
    // write, and the only thing that makes the memo safe across processes.
    static readonly ConcurrentDictionary<string, (string Rendered, DateTimeOffset At)> WrittenThisProcess = new();

    /// <summary>
    /// Records the entitlements observed for <paramref name="serverUrl"/> from a raw
    /// <c>X-Kcap-Plan</c> value. No-op for a blank URL, or when the cache already holds this answer
    /// and was refreshed within <see cref="RefreshAfter"/>. Never throws.
    /// </summary>
    public static void Set(string? serverUrl, string? headerValue, ConfigRoot config, DateTimeOffset now) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return;

        // A header that parses to nothing denied is still an ANSWER — it is how an upgrade is
        // observed — so it is written rather than skipped. Only an absent header says nothing, and
        // the handler does not call us for one.
        var rendered = PlanEntitlements.Parse(headerValue).Render();
        var key      = Normalize(serverUrl);
        var path     = PathFor(key, config);
        var at       = now;

        if (ShouldSkip(path, rendered, at)) return;

        // A temp name unique to this write. A shared one lets a peer's bytes be moved into place by
        // US: both processes write the same temp path, whoever moves first publishes whichever copy
        // landed there last, and the loser's move then fails against a file that is already gone. The
        // publisher would go on to memo a value the cache does not hold, and suppress the corrections
        // that would have fixed it.
        var tempPath = $"{path}.{Environment.ProcessId:x}.{Guid.NewGuid():N}.tmp";

        try {
            var obj = new JsonObject {
                ["url"]     = key,
                ["plan"]    = rendered,
                ["seen_at"] = at,
            };

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, obj.ToJsonString());
            // Atomic publish. Last writer wins, which is the semantics we want: the newest observation
            // of a tenant's plan is the right one, whichever process made it.
            File.Move(tempPath, path, overwrite: true);

            WrittenThisProcess[path] = (rendered, at);
        } catch {
            // Best-effort — a cache write must never break the request it rides on.
            try { File.Delete(tempPath); } catch { /* nothing to clean up, or not ours to */ }
        }
    }

    /// <summary>
    /// Whether this write can be skipped: we wrote the same answer recently AND the file still holds
    /// it. The second half is what survives a peer process — a memo alone would let us suppress our
    /// own correction while the cache carried someone else's newer, or stale, answer.
    /// </summary>
    static bool ShouldSkip(string path, string rendered, DateTimeOffset at) {
        if (!WrittenThisProcess.TryGetValue(path, out var prev)) return false;
        if (prev.Rendered != rendered || at - prev.At >= RefreshAfter) return false;

        try {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["plan"]?.GetValue<string>() == rendered;
        } catch {
            // Missing, unreadable or corrupt — rewriting is both cheap and the repair.
            return false;
        }
    }

    /// <summary>
    /// The last-observed entitlements for <paramref name="serverUrl"/>, or
    /// <see cref="PlanEntitlements.Unknown"/> when none has been seen, the file is unreadable, or the
    /// answer is older than <see cref="StaleAfter"/>.
    /// </summary>
    public static PlanEntitlements Get(string? serverUrl, ConfigRoot config, DateTimeOffset now) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return PlanEntitlements.Unknown;

        try {
            var path = PathFor(Normalize(serverUrl), config);
            if (!File.Exists(path)) return PlanEntitlements.Unknown;

            var node = JsonNode.Parse(File.ReadAllText(path));

            // A missing or unparseable timestamp is treated as stale: it cannot be shown to be
            // recent, and the safe direction here is the one that nudges.
            if (node?["seen_at"]?.GetValue<DateTimeOffset>() is not { } seenAt) return PlanEntitlements.Unknown;
            if (now - seenAt > StaleAfter) return PlanEntitlements.Unknown;

            return PlanEntitlements.Parse(node["plan"]?.GetValue<string>());
        } catch {
            return PlanEntitlements.Unknown;
        }
    }

    /// <summary>Stable cache key via the repo's own <see cref="ServerIdentity.Canonicalize"/> (path stays
    /// case-sensitive — a path-routed tenant is a distinct server), falling back to a conservative trim for
    /// a URL that isn't an admissible server base.</summary>
    internal static string Normalize(string serverUrl) =>
        ServerIdentity.Canonicalize(serverUrl) ?? serverUrl.Trim().TrimEnd('/');

    static string PathFor(string normalizedUrl, ConfigRoot config) {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl)))[..16].ToLowerInvariant();

        return config.Path($"plan-entitlements-{hash}.json");
    }
}
