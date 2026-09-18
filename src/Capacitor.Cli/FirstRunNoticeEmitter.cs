using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Builds the SessionStart fragment that tells the first session after setup that it is the first
/// one running with kcap wired in, and what to do next.
///
/// <para>Setup asks for a restart because hooks, skills and MCP servers are read at session start;
/// its own session has none of them. Saying so in the terminal works for a person watching it, and
/// is the weakest link when a tool ran the install — one line in a long transcript that it has to
/// remember to pass on. So the product says it instead, in a session that can: this fragment is
/// delivered by the hook whose presence is the thing being announced.</para>
///
/// <para>It deliberately claims no more than that. Whether this particular session reaches the
/// server is a separate question with its own notice — a rejected token already says so — and a
/// fragment asserting "you are being recorded" would be the line contradicting it.</para>
///
/// <para>It fires once: the marker is claimed under the config lock, so the first session to start
/// takes it and no later one repeats it.</para>
/// </summary>
static class FirstRunNoticeEmitter {
    /// <summary>
    /// The fragment, or null when the user opted out (<c>disable_first_run_notice</c>) or no notice
    /// is waiting — which is every session but the first after a setup, so the common answer is null
    /// and it costs one file probe.
    ///
    /// <para>Call this only where the output is going to be delivered. Resolving takes the marker,
    /// so a caller that resolves and then discards its output spends the one notice there was.</para>
    /// </summary>
    /// <param name="harness">Whose session this is, for deciding whether the tour can be offered.</param>
    public static string? Resolve(bool optedOut, ConfigRoot config, HarnessId harness, HarnessRegistry harnesses) {
        if (optedOut) return null;

        return new FirstRunNoticeStore(config).TryClaim() ? Build(ToursAreReachable(harness, harnesses)) : null;
    }

    /// <summary>
    /// The tour reads through the <c>kcap-sessions</c> and <c>kcap-analytics</c> MCP servers, so
    /// without them it is not something this user can be told to start. Setup's own Next-steps box
    /// makes the same call, for the same reason.
    /// </summary>
    static bool ToursAreReachable(HarnessId harness, HarnessRegistry harnesses) =>
        McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-sessions")
     && McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-analytics");

    internal static string Build(bool offerTour) {
        var notice =
            "## Kurrent Capacitor is set up\n" +
            "Setup finished before this session started, so this is the first session running with " +
            "kcap wired in — the one that ran setup could not be, because hooks load when a session " +
            "starts. Mention this to the user, briefly, the first time it is relevant.";

        return offerTour
            ? notice +
              "\nIf they are new to Capacitor, offer the guided tour: it walks through what their team " +
              "has recorded and the per-use-case tutorials. Start it by following the " +
              "`kcap-guided-tour` skill, or in Claude Code with `/kcap:guided-tour`."
            : notice;
    }
}
