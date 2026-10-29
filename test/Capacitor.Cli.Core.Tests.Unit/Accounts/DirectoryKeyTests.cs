using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class DirectoryKeyTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Same_directory_spelled_with_a_trailing_separator_has_one_key() {
        var dir = Tmp.CreateDir("work");

        await Assert.That(DirectoryKey.Of(dir + Path.DirectorySeparatorChar)).IsEqualTo(DirectoryKey.Of(dir));
    }

    [Test]
    public async Task Symlinked_spelling_keys_to_the_physical_directory() {
        var real = Tmp.CreateDir("real");
        var link = Tmp.PathTo("link");

        try {
            Directory.CreateSymbolicLink(link, real);
        } catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException) {
            Skip.Test("creating a symlink needs a privilege this Windows account lacks");
        }

        await Assert.That(DirectoryKey.Of(link)).IsEqualTo(DirectoryKey.Of(real));
    }

    [Test]
    public async Task Case_differs_keys_equal_except_on_linux() {
        var work = Tmp.CreateDir("Work");
        var key  = DirectoryKey.Of(work);

        if (OperatingSystem.IsLinux()) {
            await Assert.That(key).IsEqualTo(AccountDirectory.Normalize(work));
            await Assert.That(DirectoryKey.Of(Tmp.PathTo("WORK"))).IsNotEqualTo(key);
        } else {
            await Assert.That(key).IsEqualTo(key.ToLowerInvariant());
            await Assert.That(DirectoryKey.Of(Tmp.PathTo("WORK"))).IsEqualTo(key);
        }
    }
}
