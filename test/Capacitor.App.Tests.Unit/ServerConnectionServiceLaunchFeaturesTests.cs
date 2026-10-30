using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(HubTestHost))]
public class ServerConnectionServiceLaunchFeaturesTests {
    static async Task<ServerConnectionService> ConnectedAsync(HubTestHost host) {
        var lane = new ServerConnectionService(TimeProvider.System, host.Url, () => Task.FromResult<string?>(null));
        lane.Start();
        await lane.Status.Where(s => s.State == ServerLaneState.Connected).Take(1).ToTask().WaitAsync(TimeSpan.FromSeconds(30));
        return lane;
    }

    [Test]
    public async Task Features_the_server_lists_are_returned() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.LaunchFeaturesHandler = () => ["handoff/1"];
        await using var lane = await ConnectedAsync(host);

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features.Contains("handoff/1")).IsTrue();
        await Assert.That(features.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Features_from_a_server_without_the_method_are_empty() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = await ConnectedAsync(host);

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Features_while_disconnected_are_empty() {
        await using var lane = new ServerConnectionService(TimeProvider.System, "http://127.0.0.1:1", () => Task.FromResult<string?>(null));
        lane.Start();

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features.Count).IsEqualTo(0);
    }
}
