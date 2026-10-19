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

    /// <summary>Discovery and login fetch through the anonymous lane, which carries no credential but still reaches
    /// the server.</summary>
    [Test]
    public async Task An_anonymous_request_to_a_retired_origin_is_refused() {
        var root = NewRoot();
        Publish(root, Installed);
        using var sp = Container(Resolutions.AtInstallation(Url, Config.Root, LocalEndpoints.Read(root)), root);
        using var anonymous = sp.GetRequiredService<ICapacitorHttpClient>().Anonymous();

        Publish(root, Installed.RetireActive());

        await Assert.ThrowsAsync<LocalDestinationException>(() => anonymous.GetAsync($"{Url}/ping"));
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_websocket_to_a_retired_origin_is_refused() {
        var root = NewRoot();
        Publish(root, Installed.RetireActive());

        var socket = new UriBuilder(Url) { Scheme = "ws", Path = "/hubs/sessions" }.Uri;

        await Assert.That(() => LocalDestination.Check(socket, root, Url, null)).Throws<LocalDestinationException>();
    }

    /// <summary>Without a root only the resolution's own refusal is known, and it is about the server's origin: the
    /// same process still reaches every other host.</summary>
    [Test]
    public async Task A_refusal_made_at_resolution_applies_to_its_server_only() {
        const string refusal = "retired";

        await Assert.That(LocalDestination.Refusal(new Uri($"{Url}/ping"), null, Url, refusal)).IsEqualTo(refusal);
        await Assert.That(LocalDestination.Refusal(new Uri("https://registry.npmjs.org/kcap"), null, Url, refusal)).IsNull();
    }

    /// <summary>A server the request may reach redirects it to a purged installation's port: the hop is checked before
    /// it is sent, as the first request was.</summary>
    [Test]
    public async Task A_redirect_to_a_retired_port_is_refused_before_the_hop_is_sent() {
        using var retired = WireMockServer.Start();
        var root = NewRoot();
        Publish(root, InstallRegistry.Empty
            .WithActive(new(InstallationId, new Uri(retired.Urls[0]).Port, retired.Urls[0], "1.0.0")).RetireActive());

        _server.Given(Request.Create().WithPath("/moved").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{retired.Urls[0]}/ping"));
        using var sp = Container(Resolutions.At(Url, Config.Root), root);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        var refused = await Assert.ThrowsAsync<LocalDestinationException>(() => client.GetAsync($"{Url}/moved"));

        await Assert.That(refused!.Message).Contains(InstallationId);
        await Assert.That(retired.LogEntries).IsEmpty();
    }

    [Test]
    public async Task A_redirect_within_the_server_is_followed() {
        _server.Given(Request.Create().WithPath("/old").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(301).WithHeader("Location", "/ping"));
        using var sp = Container(Resolutions.At(Url, Config.Root), NewRoot());
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        using var response = await client.GetAsync($"{Url}/old");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(string.Join(",", _server.LogEntries.Select(e => e.RequestMessage.Path).Where(p => p != "/auth/config"))).IsEqualTo("/old,/ping");
    }

    /// <summary>As the runtime follows them: the credential stays with its origin, and a POST answered with a 302 is
    /// repeated as a GET.</summary>
    [Test]
    public async Task A_redirect_to_another_origin_drops_the_credential_and_turns_a_post_into_a_get() {
        using var other = WireMockServer.Start();
        other.Given(Request.Create().WithPath("/landed")).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/post").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{other.Urls[0]}/landed"));
        using var sp = Container(Resolutions.At(Url, Config.Root), NewRoot());
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/post") { Content = new StringContent("{}") };
        request.Headers.Authorization = new("Bearer", "secret");

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var landed = other.LogEntries.Single().RequestMessage;
        await Assert.That(landed.Method).IsEqualTo("GET");
        await Assert.That(landed.Headers!.ContainsKey("Authorization")).IsFalse();
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
