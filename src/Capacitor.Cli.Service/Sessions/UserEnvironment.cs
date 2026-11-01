namespace Capacitor.Cli.Service.Sessions;

public static class UserEnvironment {
    /// <summary>The logon environment with the captured one laid over it; names compare without case, as
    /// Windows compares them</summary>
    public static Dictionary<string, string> Overlay(
            IReadOnlyDictionary<string, string> logon, IReadOnlyDictionary<string, string>? captured) {
        var env = new Dictionary<string, string>(logon, StringComparer.OrdinalIgnoreCase);
        if (captured is not null)
            foreach (var (key, value) in captured) env[key] = value;
        return env;
    }

    /// <summary>The double-NUL-terminated, sorted block CreateProcessAsUser takes</summary>
    public static string Block(IReadOnlyDictionary<string, string> environment) =>
        string.Concat(environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\0")) + "\0";
}
