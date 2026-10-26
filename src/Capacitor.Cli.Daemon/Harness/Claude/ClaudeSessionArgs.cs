namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>Whether a user's own <c>claude</c> args choose the session, and which. Claude refuses
/// <c>--session-id</c> beside a flag that resumes or continues a session, so the daemon pins one only
/// when the args leave the session to Claude.</summary>
internal static class ClaudeSessionArgs {
    static readonly string[] SessionFlags = ["--continue", "--resume", "--session-id", "--fork-session", "--from-pr", "--teleport"];

    /// <summary>True when the args choose the session. Any token that could be such a flag counts, even one
    /// that is another option's value: without Claude's option table a value cannot be told from a flag, and
    /// counting it only costs the pin. <paramref name="sessionId"/> is set only when nothing else could
    /// explain the args: a lone <c>--session-id</c> or <c>--resume</c> UUID, in a position no other option's
    /// value can take — first, after a non-flag, or after a long option with an attached value.</summary>
    internal static bool SelectsSession(IReadOnlyList<string> args, out string? sessionId) {
        sessionId = null;
        string? named   = null;
        var     flags   = 0;

        for (var i = 0; i < args.Count && args[i] != "--"; i++) {
            var     arg = args[i];
            string? value;

            if (arg.StartsWith("--", StringComparison.Ordinal)) {
                var eq   = arg.IndexOf('=');
                var flag = eq < 0 ? arg : arg[..eq];
                if (!SessionFlags.Contains(flag)) continue;

                value = flag is "--session-id" or "--resume" ? eq < 0 ? Next(args, i) : arg[(eq + 1)..] : null;
            } else if (arg.Length > 1 && arg[0] == '-' && arg.AsSpan(1).ContainsAny('c', 'r')) {
                // A short-flag cluster: -c continues, -r resumes.
                value = arg == "-r" ? Next(args, i) : null;
            } else {
                continue;
            }

            flags++;
            if (i == 0 || !args[i - 1].StartsWith('-')
             || args[i - 1].StartsWith("--", StringComparison.Ordinal) && args[i - 1].Contains('=')) named = value;
        }

        if (flags == 1 && Guid.TryParseExact(named, "D", out _)) sessionId = named;

        return flags > 0;
    }

    static string? Next(IReadOnlyList<string> args, int i) =>
        i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
}
