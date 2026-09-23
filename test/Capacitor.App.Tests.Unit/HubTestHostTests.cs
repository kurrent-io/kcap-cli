using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.App.Tests.Unit;

// Static scripted handlers make host state process-global.
[NotInParallel(nameof(HubTestHost))]
public class HubTestHostTests {
    [Test]
    public async Task ClientCanInvokeAndReceiveBroadcasts() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.DaemonsHandler = () => [new DaemonInfo { Name = "work-mac", Connected = true }];

        await using var hub = new HubConnectionBuilder().WithUrl($"{host.Url}/hubs/sessions").Build();
        var changed = new TaskCompletionSource();
        hub.On(HubBroadcasts.DaemonsChanged, changed.TrySetResult);
        await hub.StartAsync();

        var daemons = await hub.InvokeAsync<List<DaemonInfo>>(HubMethods.GetConnectedDaemons);
        await Assert.That(daemons[0].Name).IsEqualTo("work-mac");

        await host.BroadcastAsync(HubBroadcasts.DaemonsChanged);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// The client's StartAsync completes on the handshake response, which the server writes before
    /// it registers the connection for Clients.All. Holding the registration back makes that gap
    /// wider than any scheduling luck: a broadcast that did not wait for admission would always
    /// reach nobody here.
    [Test]
    public async Task BroadcastWaitsForTheClientToBeAdmitted() {
        await using var host = await HubTestHost.StartAsync(admissionDelay: TimeSpan.FromMilliseconds(300));

        await using var hub = new HubConnectionBuilder().WithUrl($"{host.Url}/hubs/sessions").Build();
        var changed = new TaskCompletionSource();
        hub.On(HubBroadcasts.DaemonsChanged, changed.TrySetResult);
        await hub.StartAsync();

        await host.BroadcastAsync(HubBroadcasts.DaemonsChanged);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
