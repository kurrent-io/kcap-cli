using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Settings' Experimental tab: one switch per experimental feature, saved to the attached profile in the
/// store the CLI shares, with the daemon's own report of whether each took effect.
public sealed class ExperimentalSettingsViewModel : ReactiveObject, IDisposable {
    readonly ExperimentalSettingsStore _store;
    readonly string _profile;
    readonly CancellationTokenSource _lifetime = new();
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _attach = new(AttachState.Connecting, null, null);
    DaemonStatusDto? _snapshot;
    string? _problem;
    string? _saveProblem;

    public ExperimentalSettingsViewModel(ExperimentalSettingsStore store, string profile, IDaemonClientService service) {
        _store = store;
        _profile = profile;

        var read = store.Load(profile);
        _problem = read.Problem;
        Features = ExperimentalFeatures.All
            .Select(f => new ExperimentalFeatureRowViewModel(f, read.Readable && read.IsEnabled(f.Id), read.Readable, SaveAsync))
            .ToList();

        service.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
            _attach = status;
            if (status.State != AttachState.Connected) _snapshot = null;
            Observe();
        }).DisposeWith(_subscriptions);
        // Re-read with each snapshot: the CLI or a hand edit can flip a switch while the tab is open.
        service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(snapshot => {
            _snapshot = snapshot;
            Reload();
            Observe();
        }).DisposeWith(_subscriptions);
    }

    public IReadOnlyList<ExperimentalFeatureRowViewModel> Features { get; }

    /// Why the switches cannot change anything, or why the last change was not kept.
    public string? Problem {
        get => _problem;
        private set => this.RaiseAndSetIfChanged(ref _problem, value);
    }

    void Reload() {
        var read = _store.Load(_profile);
        foreach (var row in Features) row.Load(read.Readable && read.IsEnabled(row.Id), read.Readable);
        Problem = read.Problem ?? _saveProblem;
    }

    // Connecting, and a connection that has not yet sent a snapshot, say nothing rather than guess.
    void Observe() {
        var running = _attach.State == AttachState.Connected;
        var known = _attach.State == AttachState.Unreachable || (running && _snapshot is not null);
        foreach (var row in Features) row.Observe(running && known, known, _snapshot);
    }

    async Task SaveAsync(ExperimentalFeatureRowViewModel row, bool enabled) {
        try {
            // A refusal means the file is unreadable, which the reload reports and keeps reporting until fixed.
            _saveProblem = null;
            if (!await _store.SetAsync(_profile, row.Id, enabled, _lifetime.Token)) row.Revert(!enabled);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            _saveProblem = $"Could not save the setting: {ex.Message}";
            row.Revert(!enabled);
        }
        Reload();
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}
