using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public static class TestPluginEnvironment {
    public static PluginEnvironment For(UserHome home, string pluginDir) => new(
        Home:              home,
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => pluginDir,
        Stdout:            Console.Out,
        Stderr:            Console.Error
    ) {
        Harnesses            = TestHarnesses.Under(home),
        Binaries             = TestBinaries.None,
        ResolveMcpBinaryPath = () => "/usr/local/bin/kcap",
    };
}
