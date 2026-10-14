using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Commands;

/// <summary>Resolves the DRIVER harness — the coding agent running this MCP server — so the
/// reviewer-vendor tool can echo <c>driver_vendor</c> and the skill can recommend a reviewer that
/// differs from it. Two evidence sources, in precedence:
/// <list type="number">
///   <item><description>The <c>--driver &lt;vendor&gt;</c> stamp kcap writes into the flows MCP
///   registration for the six JSON harnesses (see <see cref="KcapMcpServers.ForHarness"/>). This is
///   the only per-process signal those harnesses give — they export no distinctive env var into the
///   long-lived MCP child.</description></item>
///   <item><description>Env inference for Claude Code / Codex, which DO export a distinctive
///   own-session variable (see <see cref="Capacitor.Cli.HarnessRequesterContext"/>); their
///   registrations are unstamped and fall through to here.</description></item>
/// </list>
/// Anything unrecognised returns null so the skill's unknown-driver fallback never claims a
/// "different model". No vendor is invented: an ambiguous (nested) env, or a stamp outside the known
/// set, stays null rather than risk naming the wrong vendor.</summary>
public static class DriverVendor {
    public static string? Infer(string? driverArg = null) => Infer(driverArg, Environment.GetEnvironmentVariable);

    /// <summary>Env-only seam kept for the existing precedence tests.</summary>
    internal static string? Infer(Func<string, string?> getEnv) => Infer(null, getEnv);

    internal static string? Infer(string? driverArg, Func<string, string?> getEnv) =>
        Infer(driverArg, getEnv, ProcessHelpers.NearestCodingAgent);

    /// <param name="nearestAgent">Asked only when a Grok session is in the environment: Grok's id is
    /// passed on to everything its processes start, so it names the driver only when Grok is nearest.</param>
    internal static string? Infer(string? driverArg, Func<string, string?> getEnv, Func<CodingAgentAncestor?> nearestAgent) {
        // Stamp wins when present and recognised — it is deterministic, unlike inherited env.
        if (Normalize(driverArg) is { } stamped) return stamped;

        // Under a Grok id the walk decides; only a Claude Code ancestor goes on to the env check below.
        if (HarnessRequesterContext.GrokSessionIn(getEnv) is not null) {
            switch (nearestAgent()?.Vendor) {
                case "grok":   return "grok";
                case "codex":  return "codex";
                case "claude": break;
                default:       return null;
            }
        }

        var claude = !string.IsNullOrWhiteSpace(getEnv(HarnessRequesterContext.ClaudeSessionIdVar));
        var codex  = !string.IsNullOrWhiteSpace(getEnv(HarnessRequesterContext.CodexThreadIdVar));

        // Co-present markers mean one harness is nested inside another — neither is provably the
        // driver, so decline to guess (mirrors HarnessRequesterContext's own nesting stance).
        if (claude && !codex) return "claude";
        if (codex && !claude) return "codex";
        return null;
    }

    // The closed set of tokens that may name a driver: the stamped JSON harnesses, the two
    // env-inferred vendors (claude/codex), and Vibe — a TOML harness outside DriverStampVendors that
    // kcap nonetheless stamps, since it exports no distinctive env var. Validating here keeps a
    // malformed or stale registration from echoing arbitrary text as driver_vendor to the model.
    static readonly HashSet<string> NameableVendors =
        new(HarnessMcpProjections.DriverStampVendors.Append("claude").Append("codex").Append("mistral-vibe").Append("grok"),
            StringComparer.Ordinal);

    static string? Normalize(string? v) =>
        !string.IsNullOrWhiteSpace(v) && NameableVendors.Contains(v.Trim()) ? v.Trim() : null;
}
