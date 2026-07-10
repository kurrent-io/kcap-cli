namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Shared length cap for the three <c>KCAP_ACP_DEBUG_FRAMES</c> call sites —
/// <c>AcpEventTranslator</c>'s unknown-update-kind dump, <c>AcpChildProcess</c>'s stderr drain, and
/// <c>AcpConnection</c>'s full inbound/outbound frame logging. Even with debug-frame logging
/// deliberately turned on, a single pathological blob (a huge tool result, a runaway diagnostic
/// line) must not itself become a log-volume/memory problem — capped independently of the
/// enclosing log line's own formatting.
///
/// The "this may contain sensitive payloads" warning for this flag is emitted once, at daemon
/// startup, by <c>DaemonRunner</c> — not lazily from these call sites — since
/// <c>DaemonConfig.DebugFrames</c> is a static, daemon-wide setting resolved once at startup, never
/// toggled mid-session.
/// </summary>
internal static class AcpDebugFrameLog {
    /// <summary>A few KB — generous for one diagnostic line while still bounding a single dump.</summary>
    const int MaxChars = 4096;

    public static string Cap(string content) =>
        content.Length <= MaxChars
            ? content
            : content[..MaxChars] + $"...[+{content.Length - MaxChars} chars truncated]";
}
