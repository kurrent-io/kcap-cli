using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// The SessionStart fragment carrying setup's restart hand-off into the next session that starts
/// with hooks in place.
///
/// <para>It claims only what the hook delivering it proves: that setup completed before this
/// session started. Not that the session reaches the server — a rejected token has its own notice,
/// and this would be the line contradicting it — and not that it is the first wired session, since
/// every setup run that installs hooks arms the marker, a re-run included.</para>
/// </summary>
static class FirstRunNoticeEmitter {
    /// <summary>
    /// The fragment, or null when the user opted out (<c>disable_first_run_notice</c>) or no notice
    /// is waiting. Opting out leaves the marker armed.
    ///
    /// <para>Call this only where the output is going to be delivered: resolving takes the marker,
    /// so a caller that resolves and then discards its output spends the one notice there was.</para>
    /// </summary>
    /// <param name="harness">Whose session this is, for deciding whether the tour can be offered.</param>
    public static string? Resolve(bool optedOut, ConfigRoot config, HarnessId harness, HarnessRegistry harnesses) {
        if (optedOut) return null;

        return new FirstRunNoticeStore(config).TryClaim() ? Build(ToursAreReachable(harness, harnesses)) : null;
    }

    // The tour reads through these two MCP servers; setup's Next-steps box gates on the same pair.
    static bool ToursAreReachable(HarnessId harness, HarnessRegistry harnesses) =>
        McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-sessions")
     && McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-analytics");

    internal static string Build(bool offerTour) {
        var notice =
            "## Kurrent Capacitor is set up\n" +
            "`kcap setup` completed before this session started, and this session is running with " +
            "kcap's hooks loaded. Hooks load when a session starts, so a session that was already " +
            "open when setup ran has none of what it installed until it restarts. Mention this to " +
            "the user, briefly, the first time it is relevant.";

        return offerTour
            ? notice +
              "\nIf they are new to Capacitor, offer the guided tour: it walks through what their team " +
              "has recorded and the per-use-case tutorials. Start it by following the " +
              "`kcap-guided-tour` skill, or in Claude Code with `/kcap:guided-tour`."
            : notice;
    }
}
