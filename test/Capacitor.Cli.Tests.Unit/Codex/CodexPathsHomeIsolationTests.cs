using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Codex;

[NotInParallel("HomeEnvVarMutation")]
public class CodexPathsHomeIsolationTests {
    [Test]
    public async Task Home_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kcap-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            // Force any prior static init that might cache HOME's value
            _ = CodexPaths.Home();

            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.Home()).IsEqualTo(Path.Combine(tmp.FullName, ".codex"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Sessions_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kcap-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            _ = CodexPaths.Sessions;
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UserHooksJson_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kcap-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        try {
            _ = CodexPaths.UserHooksJson;
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
            tmp.Delete(recursive: true);
        }
    }
}
