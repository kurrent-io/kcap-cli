namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>The environment variables that selected the Codex transport before the
/// <c>codex-app-server</c> experimental switch. A service unit installed earlier can still carry them;
/// the daemon ignores them and says so once at startup.</summary>
internal static class LegacyCodexEnvironment {
    public static readonly string[] RemovedVariables = ["KCAP_CODEX_TRANSPORT", "KCAP_CODEX_APPSERVER_INTERACTIVE"];

    public static string? Warning(Func<string, string?> getEnv) {
        var set = RemovedVariables.Where(v => getEnv(v) is { Length: > 0 }).ToArray();

        return set.Length == 0
            ? null
            : $"Warning: ignoring {string.Join(" and ", set)}; the Codex transport is now chosen with `kcap experimental enable codex-app-server`";
    }
}
