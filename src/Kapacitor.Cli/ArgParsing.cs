namespace Kapacitor.Cli;

static class ArgParsing {
    /// <summary>
    /// Resolves a positional sessionId from a command's argument list, falling
    /// back to <c>KAPACITOR_SESSION_ID</c> and then <c>CODEX_THREAD_ID</c>.
    /// Value-bearing flags (e.g. <c>--model sonnet</c>) must be declared via
    /// <paramref name="valueFlags"/> so their values aren't mistaken for the
    /// sessionId.
    /// </summary>
    internal static string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) {
        var knownValueFlags = valueFlags is null or { Length: 0 }
            ? null
            : new HashSet<string>(valueFlags, StringComparer.Ordinal);

        for (var i = skipCount; i < args.Length; i++) {
            var token = args[i];
            if (token.StartsWith("--")) {
                if (knownValueFlags?.Contains(token) == true && i + 1 < args.Length) {
                    i++; // skip the value as well
                }

                continue;
            }

            return token;
        }

        return ResolveSessionIdFromEnv();
    }

    /// <summary>
    /// Resolves a sessionId purely from environment variables. Prefers
    /// <c>KAPACITOR_SESSION_ID</c> and falls back to <c>CODEX_THREAD_ID</c>.
    /// Dashes are stripped from the returned value so callers don't have to.
    /// </summary>
    internal static string? ResolveSessionIdFromEnv() {
        var kapacitor = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID");
        if (!string.IsNullOrWhiteSpace(kapacitor))
            return kapacitor.Replace("-", "");

        var codex = Environment.GetEnvironmentVariable("CODEX_THREAD_ID");
        if (!string.IsNullOrWhiteSpace(codex))
            return codex.Replace("-", "");

        return null;
    }
}
