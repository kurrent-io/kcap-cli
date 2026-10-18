using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>A purged installation's port may be taken by anything, so nothing is sent to it: not by a process that
/// resolved it as retired, and not by one that resolved it while it was still the installation. The stub stands for
/// whatever took the port, so any logged request is a delivery that should not have happened.</summary>
public class LocalDestinationTests : IDisposable {
    const string InstallationId = "0123456789abcdef0123456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    string Url => _server.Urls[0];

    int Port => new Uri(Url).Port;

    public void Dispose() => _server.Stop();

    LocalRoot NewRoot() {
        var root = new LocalRoot(Tmp.CreateDir("local"));
        root.EnsureCreated();
        return root;
    }

    ServiceProvider Container(ProfileContext profiles, LocalRoot? root) {
        _server.Given(Request.Create().WithPath("/ping").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));

        return new ServiceCollection()
            .AddSingleton(Config.Root)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(profiles.Resolution.ServerUrl, Config.Root, profiles, root))
            .AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None)
            .BuildServiceProvider();
    }

    static void Publish(LocalRoot root, InstallRegistry registry) => LocalStateFiles.PublishRegistry(root, registry);

    InstallRegistry Installed => InstallRegistry.Empty.WithActive(new(InstallationId, Port, Url, "1.0.0"));

    [Test]
    public async Task A_command_against_a_retired_origin_is_refused_with_an_error_naming_the_installation() {
        var root = NewRoot();
        Publish(root, Installed.RetireActive());
        using var sp = Container(Resolutions.AtInstallation(Url, Config.Root, LocalEndpoints.Read(root)), root);

        var refused = await Assert.ThrowsAsync<UnusableServerUrlException>(() => sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync());

        await Assert.That(refused!.Message).Contains(InstallationId);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_hook_against_a_retired_origin_is_answered_without_a_send() {
        var root = NewRoot();
        Publish(root, Installed.RetireActive());
        using var sp = Container(Resolutions.AtInstallation(Url, Config.Root, LocalEndpoints.Read(root)), root);

        var attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.UnusableServerUrl);
        await Assert.That(attempt.Problem).Contains(InstallationId);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    /// <summary>A watcher or daemon that resolved the installation before the purge keeps its client: the send itself
    /// reads the registry again.</summary>
    [Test]
    public async Task A_process_that_resolved_the_installation_stops_sending_once_it_is_retired() {
        var root = NewRoot();
        Publish(root, Installed);
        using var sp = Container(Resolutions.AtInstallation(Url, Config.Root, LocalEndpoints.Read(root)), root);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        using (var before = await client.GetAsync($"{Url}/ping"))
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);

        Publish(root, Installed.RetireActive());

        var refused = await Assert.ThrowsAsync<LocalDestinationException>(() => client.GetAsync($"{Url}/ping"));

        await Assert.That(refused!.Message).Contains(InstallationId);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_loopback_send_while_the_registry_cannot_be_read_is_refused() {
        var root = NewRoot();
        Publish(root, Installed);
        using var sp = Container(Resolutions.AtInstallation(Url, Config.Root, LocalEndpoints.Read(root)), root);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        await File.WriteAllTextAsync(root.RegistryPath, "{not json");

        var refused = await Assert.ThrowsAsync<LocalDestinationException>(() => client.GetAsync($"{Url}/ping"));

        await Assert.That(refused!.Message).Contains(root.RegistryPath);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    /// <summary>The same sends go through when no installation ever claimed the port, which is what makes the cases
    /// above prove the registry rather than a broken fixture.</summary>
    [Test]
    public async Task A_loopback_server_that_was_never_an_installation_is_sent_to() {
        var root = NewRoot();
        using var sp = Container(Resolutions.At(Url, Config.Root), root);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();

        using var response = await client.GetAsync($"{Url}/ping");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
