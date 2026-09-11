using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class SettingsViewModel : ReactiveObject, IDisposable {
    readonly SettingsProfileStore _settings;
    readonly ILocalControlOps _ops;
    readonly string _runningName;
    readonly Func<string, CancellationToken, Task<bool>> _targetRunning;
    readonly Func<MutationRequest, CancellationToken, Task<MutationOutcome>> _runMutation;
    readonly Func<LifecyclePrompt, CancellationToken, Task<bool>> _confirm;
    readonly Func<CancellationToken, Task<bool>> _relaunch;
    readonly bool _canRenameOnPlatform;
    readonly Task _startupSettled;
    readonly bool _nameOverridden;
    readonly Func<MutationRequest, CancellationToken, Task<bool>> _canRetire;
    readonly CancellationTokenSource _lifetime;
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _status = new(AttachState.Connecting, null, null);
    DaemonStatusDto? _snapshot;
    int _savedCapacity;
    string _name;
    decimal? _capacity;
    bool _isBusy;
    bool _needsAppRestart;
    string? _message;

    public SettingsViewModel(
            SettingsProfileStore settings, IDaemonClientService service, ILocalControlOps ops,
            Func<string, CancellationToken, Task<bool>> targetRunning,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<LifecyclePrompt, CancellationToken, Task<bool>> confirm,
            Func<CancellationToken, Task<bool>> relaunch, bool canRenameOnPlatform,
            Task startupSettled, Func<MutationRequest, CancellationToken, Task<bool>> canRetire,
            bool nameOverridden = false, bool needsAppRestart = false, CancellationToken appLifetime = default) {
        _settings = settings;
        _ops = ops;
        _runningName = service.DaemonName;
        _targetRunning = targetRunning;
        _runMutation = runMutation;
        _confirm = confirm;
        _relaunch = relaunch;
        _canRenameOnPlatform = canRenameOnPlatform;
        _startupSettled = startupSettled;
        _canRetire = canRetire;
        _nameOverridden = nameOverridden;
        _needsAppRestart = needsAppRestart;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);

        var saved = settings.Load();
        _name = saved.Name ?? service.DaemonName;
        _capacity = _savedCapacity = saved.MaxAgents;

        Observable.FromAsync(startupSettled.WaitAsync).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => Refresh(), _ => Refresh()).DisposeWith(_subscriptions);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave)).DisposeWith(_subscriptions);
        RenameCommand = ReactiveCommand.CreateFromTask(RenameAsync, this.WhenAnyValue(x => x.CanRename)).DisposeWith(_subscriptions);
        service.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
            _status = status;
            if (status.State != AttachState.Connected) _snapshot = null;
            Refresh();
        }).DisposeWith(_subscriptions);
        service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(snapshot => {
            _snapshot = snapshot;
            Refresh();
        }).DisposeWith(_subscriptions);
    }

    public string Name {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); Refresh(); }
    }

    public decimal? Capacity {
        get => _capacity;
        set { this.RaiseAndSetIfChanged(ref _capacity, value); Refresh(); }
    }

    public bool IsBusy {
        get => _isBusy;
        private set { this.RaiseAndSetIfChanged(ref _isBusy, value); Refresh(); }
    }

    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public bool CanEdit => !IsBusy && !_needsAppRestart;
    public bool CanSave => CanEdit && CapacityError is null && Capacity != _savedCapacity;
    public bool CanRename => CanEdit && _canRenameOnPlatform && !_nameOverridden && _startupSettled.IsCompletedSuccessfully &&
        NameError is null && Name != DaemonStore.Sanitize(_runningName) && Idle;
    bool Idle => _status.State == AttachState.Unreachable ||
        (_status.State == AttachState.Connected && _snapshot?.Daemon.ActiveAgents == 0);

    public string? NameError => string.IsNullOrWhiteSpace(Name) || DaemonStore.Sanitize(Name) != Name
        ? "Use lowercase letters, numbers, dots, hyphens or underscores, with no surrounding spaces or repeated hyphens." : null;
    public string? CapacityError => Capacity is not { } value || value < 0 || value > int.MaxValue || decimal.Truncate(value) != value
        ? "Enter a whole number (0 = unlimited)." : null;
    public string? RenameHint => !_canRenameOnPlatform ? "Renaming is available on macOS."
        : _needsAppRestart ? "Restart this app to manage the renamed daemon."
        : _nameOverridden ? "The name is set by KCAP_DAEMON_NAME. Remove that environment override and restart the app before renaming."
        : !_startupSettled.IsCompletedSuccessfully ? "Waiting for daemon startup to finish…"
        : Name == DaemonStore.Sanitize(_runningName) ? "This is already the daemon’s service id."
        : _status.State == AttachState.Connected && _snapshot?.Daemon.ActiveAgents > 0
            ? $"Wait for the {_snapshot.Daemon.ActiveAgents} active agents to finish before renaming."
            : !Idle ? "Waiting for the daemon’s current agent count…" : "Renaming restarts the daemon and relaunches this app.";
    public string StatusLine => _status.State switch {
        AttachState.Connected when _snapshot is { } snap =>
            snap.Daemon.MaxAgents == 0
                ? $"Running as {snap.Daemon.Name}, {snap.Daemon.ActiveAgents} agents (unlimited)"
                : $"Running as {snap.Daemon.Name}, {snap.Daemon.ActiveAgents} of {snap.Daemon.MaxAgents} agents",
        AttachState.Unreachable => "Daemon not running. Changes apply when it starts.",
        _ => "Connecting to daemon…",
    };

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameCommand { get; }

    async Task SaveAsync() {
        if (!CanSave) return;
        var capacity = (int)Capacity!.Value;
        IsBusy = true;
        Message = null;
        try {
            await _settings.SaveCapacityAsync(capacity, _lifetime.Token);
            _savedCapacity = capacity;
            if (_status.State != AttachState.Connected) {
                Message = "Saved. The capacity will apply when the daemon starts.";
            } else if (_status.Capabilities?.Contains(SettingsWire.Capability) != true) {
                Message = "Saved. Update the daemon to apply capacity changes without a restart.";
            } else {
                try {
                    var ack = await _ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(capacity), _lifetime.Token);
                    Message = ack.Ok && ack.MaxAgents == capacity ? "Saved and applied to the running daemon."
                        : $"Saved. The daemon did not apply the capacity ({ack.Reason ?? "unexpected reply"}).";
                } catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested) {
                    Message = "Saved, but could not reach the daemon. The capacity will apply when it restarts.";
                }
            }
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not save settings: {ex.Message}";
        } finally { IsBusy = false; }
    }

    async Task RenameAsync() {
        if (!CanRename) return;
        var name = Name;
        IsBusy = true;
        Message = null;
        try {
            if (await _targetRunning(name, _lifetime.Token)) {
                Message = $"A daemon named {name} is already running on this machine.";
                return;
            }
            if (!await _confirm(new LifecyclePrompt(LifecyclePrompt.KindRename, null, null, false,
                    $"Rename {_runningName} to {name}? The daemon will restart and this app will relaunch. " +
                    "Only the saved capacity is used; save any capacity change first."), _lifetime.Token)) return;
            if (!_startupSettled.IsCompletedSuccessfully || !Idle) {
                Message = "Wait until startup has finished and the daemon is idle before renaming.";
                return;
            }
            var refused = MutationRequestFactory.TryBuild(MutationVerb.Replace, _settings.ProfileName,
                _settings.ServerUrl, name, out var request, DaemonStore.Sanitize(_runningName));
            if (refused is not null) {
                Message = "Could not resolve the profile for this rename. Restart the app and try again.";
                return;
            }
            if (!await _canRetire(request!, _lifetime.Token)) {
                Message = "Update the kcap command-line tool before renaming; it could not confirm support for retiring the old service. The saved name is unchanged.";
                return;
            }
            if (!_startupSettled.IsCompletedSuccessfully || !Idle) {
                Message = "Wait until startup has finished and the daemon is idle before renaming.";
                return;
            }
            var previous = await _settings.SaveNameAsync(name, _lifetime.Token);
            Message = "Name saved. Restarting the daemon…";
            var outcome = await _runMutation(request!, _lifetime.Token);
            if (outcome is MutationOutcome.Failed { Reason: "cli_unsupported" }) {
                await _settings.RestoreNameAsync(name, previous, _lifetime.Token);
                Message = "The CLI no longer supports renaming. Update kcap and try again; the saved name was restored unless another edit changed it.";
                return;
            }
            _needsAppRestart = true;
            if (outcome is not MutationOutcome.Succeeded) {
                Message = SettingsRenameMessage.For(request!, outcome);
                return;
            }
            Message = "Daemon renamed. Restart this app to connect using the new name.";
            if (await _relaunch(_lifetime.Token)) Message = "Daemon renamed. Relaunching the app…";
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Message = $"Could not finish renaming: {ex.Message} Restart the app to reload the saved settings.";
        } finally { IsBusy = false; }
    }

    void Refresh() {
        this.RaisePropertyChanged(nameof(CanEdit));
        this.RaisePropertyChanged(nameof(CanSave));
        this.RaisePropertyChanged(nameof(CanRename));
        this.RaisePropertyChanged(nameof(NameError));
        this.RaisePropertyChanged(nameof(CapacityError));
        this.RaisePropertyChanged(nameof(RenameHint));
        this.RaisePropertyChanged(nameof(StatusLine));
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}
