using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core;

/// <summary>
/// Durable, per-server cache of the tenant's plan entitlements, learned passively from the
/// <c>X-Kcap-Plan</c> response header (see <c>PlanEntitlementCaptureHandler</c>). The SessionStart
/// nudges read it so an agent is never told to use a tool its tenant's plan refuses — AI-2326, where
/// a Free tenant was nudged toward <c>declare_work_item</c> every session and every call bounced 403.
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

    // Per-process memo, so a long-lived process (the daemon, an MCP server) making many requests
    // doesn't rewrite the same file on every response.
    static readonly ConcurrentDictionary<string, string> WrittenThisProcess = new();

    /// <summary>
    /// Records the entitlements observed for <paramref name="serverUrl"/> from a raw
    /// <c>X-Kcap-Plan</c> value. No-op for a blank URL, or when the same answer was already written
    /// this process. Never throws.
    /// </summary>
    public static void Set(string? serverUrl, string? headerValue, ConfigRoot config, DateTimeOffset? now = null) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return;

        // A header that parses to nothing denied is still an ANSWER — it is how an upgrade is
        // observed — so it is written rather than skipped. Only an absent header says nothing, and
        // the handler does not call us for one.
        var rendered = PlanEntitlements.Parse(headerValue).Render();
        var key      = Normalize(serverUrl);
        var path     = PathFor(key, config);

        if (WrittenThisProcess.TryGetValue(path, out var prev) && prev == rendered) return;

        try {
            var obj = new JsonObject {
                ["url"]     = key,
                ["plan"]    = rendered,
                ["seen_at"] = now ?? DateTimeOffset.UtcNow,
            };

            var tempPath = $"{path}.tmp";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, obj.ToJsonString());
            File.Move(tempPath, path, overwrite: true);

            WrittenThisProcess[path] = rendered;
        } catch {
            // Best-effort — a cache write must never break the request it rides on.
        }
    }

    /// <summary>
    /// The last-observed entitlements for <paramref name="serverUrl"/>, or
    /// <see cref="PlanEntitlements.Unknown"/> when none has been seen, the file is unreadable, or the
    /// answer is older than <see cref="StaleAfter"/>.
    /// </summary>
    public static PlanEntitlements Get(string? serverUrl, ConfigRoot config, DateTimeOffset? now = null) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return PlanEntitlements.Unknown;

        try {
            var path = PathFor(Normalize(serverUrl), config);
            if (!File.Exists(path)) return PlanEntitlements.Unknown;

            var node = JsonNode.Parse(File.ReadAllText(path));

            // A missing or unparseable timestamp is treated as stale: it cannot be shown to be
            // recent, and the safe direction here is the one that nudges.
            if (node?["seen_at"]?.GetValue<DateTimeOffset>() is not { } seenAt) return PlanEntitlements.Unknown;
            if ((now ?? DateTimeOffset.UtcNow) - seenAt > StaleAfter) return PlanEntitlements.Unknown;

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
