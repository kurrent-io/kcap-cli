namespace Capacitor.App.Materials;

/// The library raises its failure event on the render thread; `post` moves the report to the UI thread.
public sealed class MaterialPipelineWatch : IDisposable {
    readonly Action<string> _handler;
    readonly Action<Action<string>> _unsubscribe;

    public MaterialPipelineWatch(
            IMaterialService service, Action<Action<string>> subscribe,
            Action<Action<string>> unsubscribe, Action<Action> post) {
        _handler = reason => post(() => service.ReportPipelineFailure(reason));
        _unsubscribe = unsubscribe;
        subscribe(_handler);
    }

    public void Dispose() => _unsubscribe(_handler);
}
