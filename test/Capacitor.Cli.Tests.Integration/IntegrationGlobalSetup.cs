namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Assembly-level setup that pins <c>KCAP_CONFIG_DIR</c> to an isolated
/// temp directory before any in-process test code triggers the
/// <c>PathHelpers</c> static initializer. <c>PathHelpers.ConfigDir</c> is
/// <c>static readonly</c> and captured once per process from the
/// environment, so any test that calls into <see cref="Capacitor.Cli.Commands.ClaudeHookCommand"/>
/// (or anything else that reads <c>AppConfig</c>, profile state, repo
/// exclusions, token store, …) would otherwise read the developer's real
/// <c>~/.config/kcap</c>. A user-side exclusion (e.g. <c>excluded_paths</c>
/// covering <c>/tmp/test</c> or a CI repo path) would then make the test
/// silently emit nothing and pass for the wrong reason.
///
/// Subprocess-based tests (see <see cref="McpSessionsServerTests"/>) set
/// <c>KCAP_CONFIG_DIR</c> on the child process explicitly and are not
/// affected by this parent-process value; this setup just makes the
/// in-process tests as safe as the subprocess-based ones.
/// </summary>
public class IntegrationGlobalSetup {
    internal static readonly string SharedConfigDir = Path.Combine(
        Path.GetTempPath(),
        "kcap-integration-tests-" + Guid.NewGuid().ToString("N")[..8]
    );

    [Before(Assembly)]
    public static void SetConfigDir() {
        Directory.CreateDirectory(SharedConfigDir);
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", SharedConfigDir);
    }

    [After(Assembly)]
    public static void CleanupConfigDir() {
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        try { Directory.Delete(SharedConfigDir, recursive: true); } catch { /* best effort */ }
    }
}
