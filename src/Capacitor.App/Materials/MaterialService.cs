using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Materials;

public sealed class MaterialService : IMaterialService, IDisposable {
    readonly IAppStateStore _store;
    readonly MaterialEnvironment _environment;
    readonly BehaviorSubject<MaterialState> _states;
    readonly Lock _gate = new();
    SurfaceMaterial? _requested;
    string? _failure;

    public MaterialService(IAppStateStore store, MaterialEnvironment environment, SurfaceMaterial? requested) {
        _store = store;
        _environment = environment;
        _requested = requested;
        _states = new BehaviorSubject<MaterialState>(Resolve());
    }

    public static async Task<MaterialService> LoadAsync(IAppStateStore store, MaterialEnvironment environment) {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return new MaterialService(store, environment, SurfaceMaterials.Parse(state.Material));
    }

    public MaterialState Current => _states.Value;

    public IObservable<MaterialState> States => _states.AsObservable();

    public async Task SetAsync(SurfaceMaterial material) {
        await _store.UpdateAsync(s => s with { Material = material.ToStored() }).ConfigureAwait(false);
        lock (_gate) _requested = material;
        Publish();
    }

    public void ReportPipelineFailure(string reason) {
        lock (_gate) {
            if (_failure is not null) return;
            _failure = reason;
        }
        Publish();
    }

    // Publishes are serialized with the mutations that cause them, because SetAsync resumes off
    // the UI thread and can race ReportPipelineFailure.
    void Publish() {
        lock (_gate) {
            var next = Resolve();
            if (next != _states.Value) _states.OnNext(next);
        }
    }

    MaterialState Resolve() {
        var availability = !_environment.GlassCapable ? MaterialAvailability.NotCapable
            : _failure is not null ? MaterialAvailability.PipelineFailed
            : MaterialAvailability.Available;
        var effective = availability != MaterialAvailability.Available ? SurfaceMaterial.Opaque
            : _requested ?? (_environment.ReduceTransparency ? SurfaceMaterial.Opaque : SurfaceMaterial.SoftGlass);
        var reason = availability == MaterialAvailability.PipelineFailed ? _failure : null;
        return new MaterialState(effective, _requested, availability, reason, _environment.ReduceTransparency);
    }

    public void Dispose() => _states.Dispose();
}
