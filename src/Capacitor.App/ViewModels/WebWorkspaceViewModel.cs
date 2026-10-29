using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class WebWorkspaceViewModel : ReactiveObject, IDisposable {
    readonly string? _url;
    readonly IUrlOpener _opener;
    readonly Action<string> _notify;
    readonly LocalServerRecoveryViewModel? _local;
    readonly CancellationToken _shutdown;
    readonly CompositeDisposable _disposables = new();

    public WebWorkspaceViewModel(string? serverUrl, IUrlOpener opener, Action<string> notify,
            CancellationToken shutdown, LocalServerRecoveryViewModel? local = null) {
        _url = LinkPolicy.IsOpenable(serverUrl) ? serverUrl : null;
        _opener = opener;
        _notify = notify;
        _shutdown = shutdown;
        _local = local is { IsLocalServer: true } ? local : null;

        var changes = _local is null ? Observable.Return(Unit.Default)
            : _local.WhenAnyValue(x => x.ServerDown, x => x.IsStarting, x => x.Visible,
                (_, _, _) => Unit.Default);
        _disposables.Add(changes.Subscribe(_ => this.RaisePropertyChanged(nameof(Label))));
        OpenCommand = ReactiveCommand.CreateFromTask(OpenAsync,
            changes.Select(_ => _url is not null && !(_local?.IsStarting ?? false)
                && !(_local is { ServerDown: true, Visible: false }))
                .ObserveOn(RxSchedulers.MainThreadScheduler));
        _disposables.Add(OpenCommand);
    }

    public bool IsVisible => _url is not null;
    public string Label => _local is { IsStarting: true } ? "Starting server…"
        : _local is { ServerDown: true, Visible: true } ? "Start server and open dashboard" : "Open dashboard";
    public string Tooltip => $"Open {_url} in your browser";
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    async Task OpenAsync() {
        if (_url is null || _shutdown.IsCancellationRequested) return;
        if (_local is { ServerDown: true } local) {
            if (!local.Visible) return;
            if (!await local.StartAsync()) {
                if (!_shutdown.IsCancellationRequested)
                    _notify(local.Message ?? "The local server could not be started.");
                return;
            }
        }
        if (_shutdown.IsCancellationRequested) return;
        try { _opener.Open(_url); }
        catch (Exception ex) { _notify($"Couldn't open the browser: {ex.Message}"); }
    }

    public void Dispose() => _disposables.Dispose();
}
