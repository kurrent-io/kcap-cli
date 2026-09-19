using Capacitor.App.Materials;

namespace Capacitor.App.Tests.Unit;

public class MaterialPipelineWatchTests {
    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    [Test]
    public async Task A_raised_event_becomes_one_report_posted_to_the_ui_thread() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        Action<string>? handler = null;
        var posted = new List<Action>();
        using var watch = new MaterialPipelineWatch(service, h => handler = h, _ => handler = null, posted.Add);

        handler!("shader did not compile");

        // Nothing reaches the service until the posted action runs: the event fires on the render thread.
        await Assert.That(service.Current.Availability).IsEqualTo(MaterialAvailability.Available);
        posted.Single()();
        await Assert.That(service.Current.FailureReason).IsEqualTo("shader did not compile");
    }

    [Test]
    public async Task Dispose_unsubscribes() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        Action<string>? handler = null;
        var watch = new MaterialPipelineWatch(service, h => handler = h, _ => handler = null, a => a());
        watch.Dispose();
        await Assert.That(handler).IsNull();
    }
}
