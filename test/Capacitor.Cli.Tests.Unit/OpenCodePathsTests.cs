using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodePathsTests {
    // Parallel-safe: override param is non-null, so no env var is read.
    [Test]
    public async Task ConfigDir_param_wins_over_home() {
        await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home", configDir: "/relocated/oc"))
            .IsEqualTo("/relocated/oc");
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task ConfigDir_precedence_OPENCODE_CONFIG_DIR_over_XDG_over_home() {
        var originalCfg = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try {
            // Default: neither set -> ~/.config/opencode under home.
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
            await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".config", "opencode"));

            // XDG only -> $XDG_CONFIG_HOME/opencode.
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/xdg");
            await Assert.That(OpenCodePaths.ConfigDir(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/xdg", "opencode"));

            // OPENCODE_CONFIG_DIR wins over XDG, verbatim, and KcapPlugin follows.
            var relocated = Path.Combine(Path.GetTempPath(), "kcap-oc-cfg");
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", relocated);
            await Assert.That(OpenCodePaths.ConfigDir()).IsEqualTo(relocated);
            await Assert.That(OpenCodePaths.KcapPlugin())
                .IsEqualTo(Path.Combine(relocated, "plugins", "kcap.ts"));
        } finally {
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG_DIR", originalCfg);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
        }
    }
}
