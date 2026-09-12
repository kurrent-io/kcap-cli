using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// A client aimed at the server the current run chose.
///
/// <para>The process container resolved its server once at startup — before setup could pick one,
/// and null on a first run — so every leg that runs after the choice has to build its own or it
/// authenticates against the wrong server, or against none at all. The profile name and config root
/// stay the process's, so the token lookup targets the profile it always did.</para>
/// </summary>
sealed class ChosenServerHttp(
        ConfigRoot config, ProfileContext profiles, ProfileOverrides env, MachineAuth machine) {
    public ServiceProvider For(string serverUrl, ProfileContext? chosen = null) {
        var context = chosen ?? new ProfileContext(profiles.Resolution with { ServerUrl = serverUrl }, profiles.Snapshot);

        return new ServiceCollection()
            .AddSingleton(config)
            .AddSingleton(context)
            .AddSingleton(new CapacitorServer(serverUrl, config, context))
            .AddCapacitorHttp(env, machine)
            .BuildValidated();
    }
}
