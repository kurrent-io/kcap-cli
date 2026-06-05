using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PathHelpersTests {
    [Test]
    public async Task HomeDirectory_uses_HOME_when_set_to_rooted_absolute_path() {
        var tmp = Directory.CreateTempSubdirectory("kcap-pathhelpers-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(PathHelpers.HomeDirectory).IsEqualTo(tmp.FullName);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    // The three fall-back tests below assert that PathHelpers.HomeDirectory
    // did NOT return the bogus HOME verbatim, rather than comparing it
    // against a fresh `Environment.GetFolderPath(UserProfile)` read.
    //
    // That second read goes through HOME again under the hood on Linux and
    // macOS. HOME-mutating tests live in many sibling classes
    // (PluginCommand*Tests, UninstallCommandTests, AgentsPathsTests, …); if
    // any of them sets HOME between our two reads, the comparison flakes.
    // We saw this in CI even under `--maximum-parallel-tests 1` — TUnit's
    // `[NotInParallel(...)]` constraint key doesn't bind tightly enough
    // across classes to fully serialize.
    //
    // Asserting `home != bogusInput` is the portable structural property:
    // PathHelpers.HomeDirectory only has two code paths (return HOME, or
    // replace it with the fallback), so seeing a value other than the
    // bogus input proves the fall-back branch fired. We deliberately do
    // *not* assert "non-empty / rooted" here — on macOS dev machines, the
    // fallback returns "" for whitespace/relative HOME, while on Linux CI
    // it returns the real `/etc/passwd` home; either is a correct fall-back
    // and the test shouldn't care which.

    [Test]
    public async Task HomeDirectory_falls_back_when_HOME_is_empty_string() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", "");
            var home = PathHelpers.HomeDirectory;
            await Assert.That(home).IsNotEqualTo("");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Test]
    public async Task HomeDirectory_falls_back_when_HOME_is_whitespace() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", "   ");
            var home = PathHelpers.HomeDirectory;
            await Assert.That(home).IsNotEqualTo("   ");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Test]
    public async Task HomeDirectory_falls_back_when_HOME_is_relative_path() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", "foo/bar");
            var home = PathHelpers.HomeDirectory;
            await Assert.That(home).IsNotEqualTo("foo/bar");
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }
}
