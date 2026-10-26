namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Environment variables the daemon ignores but warns about once at startup: an installed
/// service unit can still set them, and the Codex transport is chosen only by the
/// <c>codex-app-server</c> experimental feature.</summary>
internal static class LegacyCodexEnvironment {
    public static readonly string[] RemovedVariables = ["KCAP_CODEX_TRANSPORT", "KCAP_CODEX_APPSERVER_INTERACTIVE"];

    public static string? Warning(Func<string, string?> getEnv) {
        var set = RemovedVariables.Where(v => getEnv(v) is { Length: > 0 }).ToArray();

        return set.Length == 0
            ? null
            : $"Warning: ignoring {string.Join(" and ", set)}; the Codex transport is now chosen with `kcap experimental enable codex-app-server`";
    }
}
