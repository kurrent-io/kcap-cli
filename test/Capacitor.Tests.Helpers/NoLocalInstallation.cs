using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Tests.Helpers.Guards;

namespace Capacitor.Tests.Helpers;

/// <summary>A machine with no local installation, for tests that resolve a server but are not about
/// the local edition.</summary>
public static class NoLocalInstallation {
    public static LocalRoot Root => new(LocalRootGlobalSetup.AbsentLocalRoot);

    public static LocalEndpoints Endpoints => LocalEndpoints.None;
}
