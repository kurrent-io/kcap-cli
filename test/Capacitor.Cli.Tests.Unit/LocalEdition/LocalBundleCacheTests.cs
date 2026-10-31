using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>A bundle fetched ahead of the install, and the stage that takes it: only an archive whose digest matches the
/// manifest the stage verified is ever unpacked.</summary>
public class LocalBundleCacheTests {
    const string Version = "1.3.0";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("local"), Home);

    FakeBundle Bundle => field ??= new(Tmp.PathTo("bundle.tar.gz"), Version);

    string Cache => LocalBundleStager.CacheDirectory(Root);

    string CachedArchive => Path.Combine(Cache, $"kcap-local-{Version}-osx-arm64.tar.gz");

    int ArchiveRequests => Bundle.Requests.Count(r => r.EndsWith(".tar.gz", StringComparison.Ordinal));

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    LocalBundleStager Stager() => new(Root, Bundle.Client(), Bundle.Key, MacArm);

    Task FetchAsync() => Stager().FetchAsync(Version, _events, CancellationToken.None);

    void Damage() {
        Directory.CreateDirectory(Cache);
        File.WriteAllText(CachedArchive, "not the bundle");
    }

    [Test]
    public async Task A_fetch_caches_the_verified_archive_beside_its_manifest_and_signature() {
        await FetchAsync();

        await Assert.That(File.ReadAllBytes(CachedArchive)).IsEquivalentTo(File.ReadAllBytes(Tmp.PathTo("bundle.tar.gz")));
        await Assert.That(File.Exists(Path.Combine(Cache, $"kcap-local-{Version}-osx-arm64.json"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(Cache, $"kcap-local-{Version}-osx-arm64.json.sig"))).IsTrue();
        await Assert.That(_events.Reports[^1]).IsEqualTo(new LocalInstallEvent.Fetched(Version, Cache));
    }

    [Test]
    public async Task A_second_fetch_keeps_the_archive_it_already_has() {
        await FetchAsync();
        await FetchAsync();

        await Assert.That(ArchiveRequests).IsEqualTo(1);
    }

    [Test]
    public async Task A_fetch_replaces_a_damaged_archive() {
        Damage();

        await FetchAsync();

        await Assert.That(ArchiveRequests).IsEqualTo(1);
        await Assert.That(File.ReadAllBytes(CachedArchive)).IsEquivalentTo(File.ReadAllBytes(Tmp.PathTo("bundle.tar.gz")));
    }

    [Test]
    public async Task A_fetch_keeps_only_its_own_version() {
        Directory.CreateDirectory(Cache);
        var older = Path.Combine(Cache, "kcap-local-1.2.0-osx-arm64.tar.gz");
        File.WriteAllText(older, "older");

        await FetchAsync();

        await Assert.That(File.Exists(older)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_stage_takes_the_fetched_archive_without_downloading_it_then_empties_the_cache() {
        await FetchAsync();
        Bundle.Requests.Clear();

        await Stager().StageAsync(Version, "0123456789abcdef", _events, CancellationToken.None);

        await Assert.That(ArchiveRequests).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(Root.Directory, "bin", Version))).IsTrue();
        await Assert.That(Directory.Exists(Cache)).IsFalse();
    }

    /// <summary>The cache is written by whoever ran the fetch, and only the digest the stage verified vouches for it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_cached_archive_that_does_not_match_the_verified_manifest_is_downloaded_again() {
        Root.EnsureCreated();
        Damage();

        await Stager().StageAsync(Version, "0123456789abcdef", _events, CancellationToken.None);

        await Assert.That(ArchiveRequests).IsEqualTo(1);
        await Assert.That(Directory.Exists(Path.Combine(Root.Directory, "bin", Version))).IsTrue();
        await Assert.That(Directory.Exists(Cache)).IsFalse();
    }
}
