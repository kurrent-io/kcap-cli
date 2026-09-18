using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Builds the SessionStart fragment that tells the first session after setup that recording is on,
/// and what to do next.
///
/// <para>Setup asks for a restart because hooks, skills and MCP servers are read at session start;
/// its own session has none of them. Saying so in the terminal works for a person watching it, and
/// is the weakest link when a tool ran the install — one line in a long transcript that it has to
/// remember to pass on. So the product says it instead, in the session that can prove it: this
/// fragment only appears where hooks are actually loaded, which is the thing being announced.</para>
///
/// <para>It fires once. The marker is claimed by an atomic rename, so the first session to start
/// takes it and no later one repeats it.</para>
/// </summary>
static class FirstRunNoticeEmitter {
    /// <summary>
    /// The fragment, or null when the user opted out (<c>disable_first_run_notice</c>) or no notice
    /// is waiting — which is every session but the first after a setup, so the common answer is null
    /// and it costs one file probe.
    /// </summary>
    public static string? Resolve(bool optedOut, ConfigRoot config) {
        if (optedOut) return null;

        return new FirstRunNoticeStore(config).TryClaim() ? Build() : null;
    }

    internal static string Build() =>
        "## Capacitor is recording\n" +
        "Setup finished before this session started, so this is the first session Kurrent Capacitor " +
        "is recording — the one that ran setup could not be, because hooks load when a session " +
        "starts. Tell the user this, briefly, the first time it is relevant.\n" +
        "If they are new to Capacitor, offer the guided tour: it walks through what their team has " +
        "recorded and the per-use-case tutorials. Start it by following the `kcap-guided-tour` " +
        "skill, or in Claude Code with `/kcap:guided-tour`.";
}
