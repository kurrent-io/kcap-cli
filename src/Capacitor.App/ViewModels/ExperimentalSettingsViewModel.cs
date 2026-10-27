using System.Reactive.Concurrency;
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
    long _readSequence;
    long _appliedSequence;

    public ExperimentalSettingsViewModel(ExperimentalSettingsStore store, string profile, IDaemonClientService service) {
        _store = store;
        _profile = profile;

        // The switches stay locked until the first read lands; it runs off the UI thread like every other.
        Features = ExperimentalFeatures.All
            .Select(f => new ExperimentalFeatureRowViewModel(f, false, false, SaveAsync))
            .ToList();

        service.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
            _attach = status;
            if (status.State != AttachState.Connected) _snapshot = null;
            Observe();
        }).DisposeWith(_subscriptions);
        // Re-read with each snapshot, on the thread pool whichever thread emitted it: the CLI or a hand edit
        // can flip a switch while the tab is open. ObserveOn keeps snapshot order, so reads stay in sequence.
        service.Snapshots
            .ObserveOn(TaskPoolScheduler.Default)
            .Select(snapshot => (Snapshot: snapshot, Read: ReadIfChanged()))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(next => {
                _snapshot = next.Snapshot;
                if (next.Read is { } read) Apply(read);
                Observe();
            }).DisposeWith(_subscriptions);

        Loaded = LoadAsync();
    }

    /// Completes once the store's first read has been applied.
    internal Task Loaded { get; }

    public IReadOnlyList<ExperimentalFeatureRowViewModel> Features { get; }

    /// Why the switches cannot change anything, or why the last change was not kept.
    public string? Problem {
        get => _problem;
        private set => this.RaiseAndSetIfChanged(ref _problem, value);
    }

    /// The store as it is now, or null when its last-write time and length match the last read.
    internal (ExperimentalSettingsRead Settings, long Epoch, long Sequence)? ReadIfChanged() => Read(onlyIfChanged: true);

    // The sequence is taken with the fingerprint, so it orders reads by the file state each one recorded.
    (ExperimentalSettingsRead Settings, long Epoch, long Sequence)? Read(bool onlyIfChanged) {
        var epoch = Interlocked.Read(ref _saveEpoch);
        var fingerprint = Fingerprint();
        long sequence;
        lock (_fingerprintLock) {
            if (onlyIfChanged && fingerprint == _fingerprint) return null;
            _fingerprint = fingerprint;
            sequence = ++_readSequence;
        }
        return (_store.Load(_profile), epoch, sequence);
    }

    /// A read that a save began or ended after is stale, and a row being saved keeps the value the user
    /// just chose; the next snapshot reads again. A read older than one already applied is dropped: the
    /// fingerprint already matches the newer file, so nothing would read it again.
    internal void Apply((ExperimentalSettingsRead Settings, long Epoch, long Sequence) next) {
        var (read, epoch, sequence) = next;
        if (sequence <= _appliedSequence) return;
        if (epoch != Interlocked.Read(ref _saveEpoch)) {
            lock (_fingerprintLock) _fingerprint = null;
            return;
        }
        _appliedSequence = sequence;
        foreach (var row in Features) {
            if (_saving.Contains(row.Id)) continue;
            row.Load(read.Readable && read.IsEnabled(row.Id), read.Readable);
        }
        Problem = read.Problem ?? _saveProblem;
    }

    // Reads off the UI thread; constructed and saved on it, so each continuation applies back on it.
    async Task LoadAsync() {
        Apply((await Task.Run(() => Read(onlyIfChanged: false)))!.Value);
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

        await LoadAsync();
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}
