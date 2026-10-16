using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalRootTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Defaults_to_a_fixed_directory_under_home() {
        var root = LocalRoot.Resolve(null, new UserHome("/home/me"));

        await Assert.That(root.Directory).IsEqualTo(Path.Combine("/home/me", ".config", "kcap", "local"));
    }

    /// <summary>The server trims and absolutizes the override the same way; a difference would put the
    /// CLI's journal somewhere the server does not look.</summary>
    [Test]
    public async Task An_override_is_absolutized_and_trimmed_as_the_server_does() {
        var configured = Tmp.PathTo("stack") + Path.DirectorySeparatorChar;

        var root = LocalRoot.Resolve(configured, new UserHome("/home/me"));

        await Assert.That(root.Directory).IsEqualTo(Tmp.PathTo("stack"));
    }

    [Test]
    public async Task EnsureCreated_makes_the_root_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var root = new LocalRoot(Tmp.CreateDir("local"));
        File.SetUnixFileMode(root.Directory, (UnixFileMode)0b111_101_101);

        root.EnsureCreated();

        await Assert.That(File.GetUnixFileMode(root.Directory))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task EnsureCreated_refuses_a_symlinked_root() {
        var target = Tmp.CreateDir("elsewhere").Path;
        var link   = Tmp.PathTo("local");
        Directory.CreateSymbolicLink(link, target);

        await Assert.That(() => new LocalRoot(link).EnsureCreated()).Throws<LocalLifecycleException>();
    }
}
