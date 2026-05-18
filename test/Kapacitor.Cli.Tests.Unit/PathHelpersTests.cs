using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PathHelpersTests {
    [Test]
    public async Task HomeDirectory_uses_HOME_when_set_to_rooted_absolute_path() {
        var tmp = Directory.CreateTempSubdirectory("kapacitor-pathhelpers-test-");
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", tmp.FullName);
            await Assert.That(PathHelpers.HomeDirectory).IsEqualTo(tmp.FullName);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task HomeDirectory_falls_back_when_HOME_is_empty_string() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");

        try {
            Environment.SetEnvironmentVariable("HOME", "");
            var home = PathHelpers.HomeDirectory;
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Assert.That(home).IsEqualTo(expected);
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
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Assert.That(home).IsEqualTo(expected);
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
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await Assert.That(home).IsEqualTo(expected);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }
}
