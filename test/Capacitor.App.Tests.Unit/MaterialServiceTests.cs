using Capacitor.App.Materials;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class MaterialServiceTests {
    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    [Test]
    public async Task No_choice_on_a_capable_machine_is_soft_glass() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, requested: null);
        await Assert.That(service.Current).IsEqualTo(
            new MaterialState(SurfaceMaterial.SoftGlass, null, MaterialAvailability.Available, null, false));
    }

    [Test]
    public async Task No_choice_with_reduce_transparency_is_opaque() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, requested: null);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.ReduceTransparency).IsTrue();
    }

    [Test]
    public async Task An_explicit_glass_choice_beats_reduce_transparency() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, SurfaceMaterial.LiquidGlass);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.LiquidGlass);
    }

    [Test]
    public async Task A_machine_that_cannot_do_glass_is_opaque_and_keeps_the_request() {
        var service = new MaterialService(new InMemoryAppStateStore(), new MaterialEnvironment(false, false), SurfaceMaterial.SoftGlass);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.Requested).IsEqualTo(SurfaceMaterial.SoftGlass);
        await Assert.That(service.Current.Availability).IsEqualTo(MaterialAvailability.NotCapable);
    }

    [Test]
    public async Task Set_persists_the_stored_name_and_publishes() {
        var store = new InMemoryAppStateStore();
        var service = new MaterialService(store, Mac, requested: null);
        var seen = new List<MaterialState>();
        using var _ = service.States.Subscribe(seen.Add);

        await service.SetAsync(SurfaceMaterial.LiquidGlass);

        await Assert.That(store.State.Material).IsEqualTo("liquid_glass");
        await Assert.That(seen[^1].Effective).IsEqualTo(SurfaceMaterial.LiquidGlass);
        await Assert.That(seen[^1].Requested).IsEqualTo(SurfaceMaterial.LiquidGlass);
    }

    [Test]
    public async Task A_failed_write_still_applies_for_the_run() {
        var service = new MaterialService(new InMemoryAppStateStore(writesSucceed: false), Mac, requested: null);
        await service.SetAsync(SurfaceMaterial.Opaque);
        await Assert.That(service.Current.Effective).IsEqualTo(SurfaceMaterial.Opaque);
    }

    [Test]
    public async Task A_pipeline_failure_latches_opaque_with_its_reason_and_keeps_the_request() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        service.ReportPipelineFailure("shader did not compile");
        service.ReportPipelineFailure("a later, ignored reason");

        await Assert.That(service.Current).IsEqualTo(new MaterialState(
            SurfaceMaterial.Opaque, SurfaceMaterial.SoftGlass, MaterialAvailability.PipelineFailed, "shader did not compile", false));
    }

    [Test]
    public async Task A_failure_publishes_even_when_the_effective_material_does_not_move() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.Opaque);
        var seen = new List<MaterialState>();
        using var _ = service.States.Subscribe(seen.Add);

        service.ReportPipelineFailure("no lease");

        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[1].Availability).IsEqualTo(MaterialAvailability.PipelineFailed);
    }

    [Test]
    public async Task A_late_subscriber_gets_the_current_state() {
        var service = new MaterialService(new InMemoryAppStateStore(), Mac, requested: null);
        service.ReportPipelineFailure("no lease");
        MaterialState? first = null;
        using var _ = service.States.Subscribe(s => first ??= s);
        await Assert.That(first!.Availability).IsEqualTo(MaterialAvailability.PipelineFailed);
    }

    [Test]
    public async Task Load_reads_the_stored_choice_leniently() {
        var known = await MaterialService.LoadAsync(new InMemoryAppStateStore(new AppState(Material: "opaque")), Mac);
        var unknown = await MaterialService.LoadAsync(new InMemoryAppStateStore(new AppState(Material: "frosted_titanium")), Mac);
        await Assert.That(known.Current.Requested).IsEqualTo(SurfaceMaterial.Opaque);
        await Assert.That(unknown.Current.Requested).IsNull();
        await Assert.That(unknown.Current.Effective).IsEqualTo(SurfaceMaterial.SoftGlass);
    }
}
