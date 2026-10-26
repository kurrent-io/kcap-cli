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
    readonly HashSet<string> _saving = new(StringComparer.Ordinal);
    readonly Lock _fingerprintLock = new();
    (bool Exists, DateTime Written, long Length)? _fingerprint;
    long _saveEpoch;

    public ExperimentalSettingsViewModel(ExperimentalSettingsStore store, string profile, IDaemonClientService service) {
        _store = store;
        _profile = profile;

        _fingerprint = Fingerprint();
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
        // Re-read with each snapshot, before it reaches the UI thread: the CLI or a hand edit can flip a
        // switch while the tab is open.
        service.Snapshots
            .Select(snapshot => (Snapshot: snapshot, Read: ReadIfChanged()))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(next => {
                _snapshot = next.Snapshot;
                if (next.Read is { } read) Apply(read.Settings, read.Epoch);
                Observe();
            }).DisposeWith(_subscriptions);
    }

    public IReadOnlyList<ExperimentalFeatureRowViewModel> Features { get; }

    /// Why the switches cannot change anything, or why the last change was not kept.
    public string? Problem {
        get => _problem;
        private set => this.RaiseAndSetIfChanged(ref _problem, value);
    }

    /// The store as it is now, or null when its last-write time and length match the last read.
    internal (ExperimentalSettingsRead Settings, long Epoch)? ReadIfChanged() {
        var epoch = Interlocked.Read(ref _saveEpoch);
        var fingerprint = Fingerprint();
        lock (_fingerprintLock) {
            if (fingerprint == _fingerprint) return null;
            _fingerprint = fingerprint;
        }
        return (_store.Load(_profile), epoch);
    }

    /// A read that a save began or ended after is stale, and a row being saved keeps the value the user
    /// just chose; the next snapshot reads again.
    internal void Apply(ExperimentalSettingsRead read, long epoch) {
        if (epoch != Interlocked.Read(ref _saveEpoch)) {
            lock (_fingerprintLock) _fingerprint = null;
            return;
        }
        foreach (var row in Features) {
            if (_saving.Contains(row.Id)) continue;
            row.Load(read.Readable && read.IsEnabled(row.Id), read.Readable);
        }
        Problem = read.Problem ?? _saveProblem;
    }

    (bool, DateTime, long) Fingerprint() {
        var file = new FileInfo(_store.Path);
        return file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0);
    }

    // Connecting, and a connection that has not yet sent a snapshot, say nothing rather than guess.
    void Observe() {
        var running = _attach.State == AttachState.Connected;
        var known = _attach.State == AttachState.Unreachable || (running && _snapshot is not null);
        foreach (var row in Features) row.Observe(running && known, known, _snapshot);
    }

    async Task SaveAsync(ExperimentalFeatureRowViewModel row, bool enabled) {
        _saving.Add(row.Id);
        Interlocked.Increment(ref _saveEpoch);
        try {
            // A refusal means the file is unreadable, which the reload reports and keeps reporting until fixed.
            _saveProblem = null;
            if (!await _store.SetAsync(_profile, row.Id, enabled, _lifetime.Token)) row.Revert(!enabled);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            _saveProblem = $"Could not save the setting: {ex.Message}";
            row.Revert(!enabled);
        } finally {
            _saving.Remove(row.Id);
            Interlocked.Increment(ref _saveEpoch);
        }

        lock (_fingerprintLock) _fingerprint = Fingerprint();
        Apply(_store.Load(_profile), Interlocked.Read(ref _saveEpoch));
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}
