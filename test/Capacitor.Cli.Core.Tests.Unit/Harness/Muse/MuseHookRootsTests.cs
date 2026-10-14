using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseHookRootsTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    [Test]
    public async Task A_hook_with_a_cleared_environment_resolves_the_persisted_roots() {
        var data   = Tmp.PathTo("data");
        var config = Tmp.PathTo("config");
        await Assert.That(MuseHookRoots.Persist(Home, new MusePaths(Home, config, data))).IsTrue();

        var paths = MuseHarness.FromEnvironment(Home, _ => null).Paths;

        await Assert.That(paths.DataRoot).IsEqualTo(Path.Combine(data, "muse"));
        await Assert.That(paths.ConfigRoot).IsEqualTo(Path.Combine(config, "muse"));
    }

    [Test]
    public async Task A_set_variable_wins_over_the_persisted_root() {
        MuseHookRoots.Persist(Home, new MusePaths(Home, null, Tmp.PathTo("persisted")));
        var shell = Tmp.PathTo("shell");

        var paths = MuseHarness.FromEnvironment(Home, k => k == "XDG_DATA_HOME" ? shell : null).Paths;

        await Assert.That(paths.DataRoot).IsEqualTo(Path.Combine(shell, "muse"));
    }

    [Test]
    public async Task Default_roots_persist_nothing_and_clear_an_earlier_file() {
        MuseHookRoots.Persist(Home, new MusePaths(Home, null, Tmp.PathTo("data")));
        await Assert.That(File.Exists(MuseHookRoots.PathFor(Home))).IsTrue();

        await Assert.That(MuseHookRoots.Persist(Home, new MusePaths(Home, null, null))).IsTrue();

        await Assert.That(File.Exists(MuseHookRoots.PathFor(Home))).IsFalse();
        await Assert.That(MuseHarness.FromEnvironment(Home, _ => null).Paths.DataRoot)
            .IsEqualTo(new MusePaths(Home, null, null).DataRoot);
    }

    [Test]
    public async Task Only_the_root_that_differs_from_the_default_is_persisted() {
        var data = Tmp.PathTo("data");

        MuseHookRoots.Persist(Home, new MusePaths(Home, Path.Combine(Home.Path, ".config"), data));

        await Assert.That(MuseHookRoots.Read(Home)).IsEqualTo(((string?)null, (string?)data));
    }

    [Test]
    public async Task An_unreadable_file_reads_as_nothing_persisted() {
        var path = MuseHookRoots.PathFor(Home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ not json");

        await Assert.That(MuseHookRoots.Read(Home)).IsEqualTo(((string?)null, (string?)null));
    }
}
