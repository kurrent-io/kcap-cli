using System.Runtime.InteropServices;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The exit codes and the JSON lines the desktop app reads from <c>kcap local fetch --json</c>.</summary>
[NotInParallel]
public class LocalFetchCommandTests {
    const string Version = "1.3.0";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    sealed class StackFactory(LocalRoot root, Func<LocalBundleStager> stager) : ILocalStackFactory {
        public LocalRoot Root => root;

        public LocalStack Create() => throw new InvalidOperationException("A fetch never touches the stack.");

        public LocalBundleStager Stager() => stager();
    }

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("local"), Home);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    async Task<(int Exit, string Out)> RunAsync(FakeBundle bundle, LocalPlatform? platform = null) {
        var factory = new StackFactory(Root, () => new(Root, bundle.Client(), bundle.Key, MacArm));

        using var capture = ConsoleOutput.StartFullCapture();

        var exit = await new LocalFetchCommand(factory) { Version = Version, Platform = platform ?? MacArm }.HandleAsync(["local", "fetch", "--json"]);

        return (exit, capture.GetCapturedOutput());
    }

    [Test]
    public async Task A_fetch_ends_with_the_fetched_line_and_exits_0() {
        using var bundle = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Version);

        var (exit, output) = await RunAsync(bundle);

        await Assert.That(exit).IsEqualTo(LocalFetchCommand.Fetched);
        await Assert.That(output.Trim().Split('\n')[^1].Trim()).StartsWith("""{"v":1,"event":"fetched","version":"1.3.0","path":""");
    }

    /// <summary>A CLI release can reach its channel before its bundle; the app then installs without a prefetch.</summary>
    [Test]
    public async Task An_unpublished_bundle_exits_4() {
        using var bundle = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Version) { ManifestMissing = true };

        var (exit, output) = await RunAsync(bundle);

        await Assert.That(exit).IsEqualTo(LocalFetchCommand.NotPublished);
        await Assert.That(output).Contains("\"code\":\"bundle_not_published\"");
    }

    [Test]
    public async Task An_installation_already_at_this_version_has_nothing_to_fetch() {
        using var bundle = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Version);
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root,
            InstallRegistry.Empty.WithActive(new("0123456789abcdef0123456789abcdef", 47391, "http://localhost:47391", Version)));
        Directory.CreateDirectory(Path.Combine(Root.Directory, "bin", Version));

        var (exit, output) = await RunAsync(bundle);

        await Assert.That(exit).IsEqualTo(LocalFetchCommand.NothingToDo);
        await Assert.That(output).Contains("\"reason\":\"current\"");
        await Assert.That(bundle.Requests).IsEmpty();
    }

    [Test]
    public async Task A_machine_without_a_local_edition_build_is_refused() {
        using var bundle = new FakeBundle(Tmp.PathTo("bundle.tar.gz"), Version);

        var (exit, output) = await RunAsync(bundle, new LocalPlatform(false, true, false, Architecture.X64));

        await Assert.That(exit).IsEqualTo(LocalFetchCommand.Failed);
        await Assert.That(output).Contains("\"code\":\"unsupported_platform\"");
    }
}
