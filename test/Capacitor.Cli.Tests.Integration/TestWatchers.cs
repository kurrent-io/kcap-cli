using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The watcher manager for a test that builds its command by hand rather than resolving one. The
/// starter is the real one: a test that reaches a spawn substitutes it and says so at the call.
/// </summary>
static class TestWatchers {
    public static WatcherManager For(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) =>
        new(config, profiles, http, SystemProcessStarter.Instance);
}
