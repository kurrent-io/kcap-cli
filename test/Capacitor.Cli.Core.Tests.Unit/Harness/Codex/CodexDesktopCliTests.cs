using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>Which executable in the ChatGPT app's bundled Codex package is launched. The layout is the
/// macOS bundle's, whose entrypoint carries no extension, so Windows cannot launch it and skips.</summary>
public class CodexDesktopCliTests {
    [TempDir] public required TempDir Apps { get; init; }

    DesktopAppProbe Probe => DesktopAppProbe.Searching(Apps.Path);

    static readonly string Package = Path.Combine("ChatGPT.app", "Contents", "Resources", "codex-cli");

    string Stage(string relative) => Apps.CreateExecutable(Path.Combine(Package, relative), "#!/bin/sh\n");

    void Manifest(string json) => Apps.CreateFile(Path.Combine(Package, "codex-package.json"), json);

    [Before(Test)]
    public void NotOnWindows() => Skip.When(OperatingSystem.IsWindows(), "macOS bundle layout");

    [Test]
    public async Task Launches_the_entrypoint_the_manifest_names() {
        Stage(Path.Combine("bin", "codex"));
        var named = Stage(Path.Combine("CodexCLI.app", "Contents", "MacOS", "codex"));
        Manifest("""{"layoutVersion":1,"entrypoint":"CodexCLI.app/Contents/MacOS/codex"}""");

        await Assert.That(CodexDesktopCli.Find(Probe)).IsEqualTo(named);
    }

    [Test]
    public async Task Falls_back_to_bin_codex_without_a_readable_manifest() {
        var entry = Stage(Path.Combine("bin", "codex"));
        Manifest("not json");

        await Assert.That(CodexDesktopCli.Find(Probe)).IsEqualTo(entry);
    }

    /// <summary>The manifest decides what the daemon spawns, so it cannot point outside the bundle.
    /// The precondition: the target it names is itself launchable.</summary>
    [Test]
    public async Task Refuses_an_entrypoint_outside_the_package() {
        var outside = Apps.CreateExecutable("evil", "#!/bin/sh\n");
        Manifest("""{"entrypoint":"../../../../evil"}""");

        await Assert.That(BinaryProbe.Searching(null).Resolve(outside)).IsNotNull();
        await Assert.That(CodexDesktopCli.Find(Probe)).IsNull();
    }

    /// <summary>A link inside the package can point anywhere, so neither a linked entrypoint nor a
    /// linked directory on the way to it is followed.</summary>
    [Test]
    public async Task Refuses_an_entrypoint_reached_through_a_symlink() {
        var outside = Apps.CreateExecutable(Path.Combine("elsewhere", "codex"), "#!/bin/sh\n");
        Apps.CreateDir(Package, "bin");
        File.CreateSymbolicLink(Apps.PathTo(Package, "bin", "codex"), outside);

        await Assert.That(CodexDesktopCli.Find(Probe)).IsNull();

        File.Delete(Apps.PathTo(Package, "bin", "codex"));
        Directory.Delete(Apps.PathTo(Package, "bin"));
        Directory.CreateSymbolicLink(Apps.PathTo(Package, "bin"), Apps.PathTo("elsewhere"));

        await Assert.That(CodexDesktopCli.Find(Probe)).IsNull();
    }

    [Test]
    public async Task Finds_nothing_when_the_entrypoint_is_missing() {
        Manifest("""{"entrypoint":"bin/codex"}""");

        await Assert.That(CodexDesktopCli.Find(Probe)).IsNull();
        await Assert.That(CodexDesktopCli.Find(DesktopAppProbe.None)).IsNull();
    }
}
