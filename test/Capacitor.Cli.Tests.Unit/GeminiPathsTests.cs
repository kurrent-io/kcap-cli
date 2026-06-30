using Capacitor.Cli.Core.Gemini;

namespace Capacitor.Cli.Tests.Unit;

public class GeminiPathsTests {
    // Parallel-safe: the override param is non-null, so no env var is read.
    [Test]
    public async Task Root_gemini_cli_home_param_is_parent_of_dot_gemini() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: "/foo"))
            .IsEqualTo(Path.Combine("/foo", ".gemini"));
    }

    [Test]
    public async Task Root_defaults_to_dot_gemini_under_home() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: null))
            .IsEqualTo(Path.Combine("/fake/home", ".gemini"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Root_reads_GEMINI_CLI_HOME_and_ignores_GEMINI_HOME() {
        var originalCli = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        var originalOld = Environment.GetEnvironmentVariable("GEMINI_HOME");
        try {
            // The defunct GEMINI_HOME must NOT be honored.
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", "/should/be/ignored");
            await Assert.That(GeminiPaths.Root(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".gemini"));

            // GEMINI_CLI_HOME is the parent of .gemini, and SettingsJson follows.
            var parent = Path.Combine(Path.GetTempPath(), "kcap-gemini-cfg");
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", parent);
            await Assert.That(GeminiPaths.Root()).IsEqualTo(Path.Combine(parent, ".gemini"));
            await Assert.That(GeminiPaths.SettingsJson())
                .IsEqualTo(Path.Combine(parent, ".gemini", "settings.json"));
        } finally {
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", originalCli);
            Environment.SetEnvironmentVariable("GEMINI_HOME", originalOld);
        }
    }
}
