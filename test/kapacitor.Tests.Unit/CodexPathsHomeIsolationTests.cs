using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace kapacitor.Tests.Unit;

public class CodexPathsHomeIsolationTests {
    [Test]
    public async Task Home_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            // Force any prior static init that might cache HOME's value
            _ = CodexPaths.Home;

            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.Home).IsEqualTo(Path.Combine(tmp.FullName, ".codex"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Sessions_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = CodexPaths.Sessions;
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.Sessions).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "sessions"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task UserHooksJson_reflects_current_HOME_env_var() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-codexpaths-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            _ = CodexPaths.UserHooksJson;
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(Path.Combine(tmp.FullName, ".codex", "hooks.json"));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }
}
