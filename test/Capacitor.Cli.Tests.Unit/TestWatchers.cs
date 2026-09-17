using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The watcher manager for a test that builds its command by hand rather than resolving one. The
/// starter and the spawner are the real ones: a test that reaches either substitutes it and says so
/// at the call.
/// </summary>
static class TestWatchers {
    public static WatcherManager For(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
        var paths   = WatcherPaths.FromEnvironment(config);
        var starter = SystemProcessStarter.Instance;

        return new WatcherManager(
            config, profiles, http, starter, paths, new ProcessWatcherSpawner(config, profiles, paths, starter, TimeProvider.System), TimeProvider.System);
    }

    /// <summary>A manager whose spawn decisions the test observes instead of launching.</summary>
    public static WatcherManager For(
            ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, IWatcherSpawner spawner) =>
        new(config, profiles, http, SystemProcessStarter.Instance, WatcherPaths.FromEnvironment(config), spawner, TimeProvider.System);
    /// <summary>A manager over a directory the test names, rather than the one the environment does.</summary>
    public static WatcherManager In(
            WatcherPaths paths, ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) =>
        new(config, profiles, http, SystemProcessStarter.Instance, paths,
            new ProcessWatcherSpawner(config, profiles, paths, SystemProcessStarter.Instance, TimeProvider.System), TimeProvider.System);
}
