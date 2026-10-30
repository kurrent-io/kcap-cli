using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(HubTestHost))]
public class ServerConnectionServiceLaunchFeaturesTests {
    static async Task<ServerConnectionService> ConnectedAsync(HubTestHost host, TimeProvider? time = null) {
        var lane = new ServerConnectionService(time ?? TimeProvider.System, host.Url, () => Task.FromResult<string?>(null));
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

        await Assert.That(features!.Contains("handoff/1")).IsTrue();
        await Assert.That(features.Count).IsEqualTo(1);
    }

    /// The two answers a server gives for a hub method it lacks, matched only for this method: a
    /// handler's own HubException that happens to say the same words is no answer.
    [Test]
    [Arguments("Unknown hub method 'GetLaunchFeatures'", true)]
    [Arguments("Failed to invoke 'GetLaunchFeatures' due to an error on the server. HubException: Method does not exist.", true)]
    [Arguments("Unknown hub method 'RequestLaunchAgentV2'", false)]
    [Arguments("Failed to invoke 'RequestLaunchAgentV2' due to an error on the server. HubException: Method does not exist.", false)]
    [Arguments("An unexpected error occurred invoking 'GetLaunchFeatures' on the server. HubException: Method does not exist", false)]
    public async Task Only_a_missing_launch_features_method_reads_as_an_older_server(string message, bool missing) {
        await Assert.That(ServerConnectionService.IsMissingMethod(new HubException(message), HubMethods.GetLaunchFeatures))
            .IsEqualTo(missing);
    }

    [Test]
    public async Task Features_from_a_handlers_own_hub_exception_are_unknown() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = await ConnectedAsync(host);

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features).IsNull();
    }

    [Test]
    public async Task Features_from_a_call_that_never_answers_are_unknown_after_ten_seconds() {
        await using var host = await HubTestHost.StartAsync();
        using var entered = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        HubTestHost.LaunchFeaturesHandler = () => {
            entered.Release();
            release.Wait(TimeSpan.FromSeconds(60));
            return ["handoff/1"];
        };
        var time = new FakeTimeProvider();
        await using var lane = await ConnectedAsync(host, time);
        try {
            var reading = ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);
            await entered.WaitAsync(TimeSpan.FromSeconds(30));

            time.Advance(TimeSpan.FromSeconds(9));
            await Assert.That(reading.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromSeconds(1));

            await Assert.That(await reading.WaitAsync(TimeSpan.FromSeconds(30))).IsNull();
        } finally {
            release.Set();
        }
    }

    [Test]
    public async Task Features_from_a_failed_call_are_unknown() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.LaunchFeaturesHandler = () => throw new InvalidOperationException("boom");
        await using var lane = await ConnectedAsync(host);

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features).IsNull();
    }

    [Test]
    public async Task Features_while_disconnected_are_unknown() {
        await using var lane = new ServerConnectionService(TimeProvider.System, "http://127.0.0.1:1", () => Task.FromResult<string?>(null));
        lane.Start();

        var features = await ((ILaunchClient)lane).GetLaunchFeaturesAsync(CancellationToken.None);

        await Assert.That(features).IsNull();
    }
}
