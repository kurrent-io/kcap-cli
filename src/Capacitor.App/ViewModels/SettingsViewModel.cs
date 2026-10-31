using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Notifications;
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
    readonly Func<CancellationToken, Task<MutationOutcome?>>? _moveToWindowsService;
    readonly Func<LifecyclePrompt, CancellationToken, Task<bool>> _confirm;
    readonly Func<CancellationToken, Task<bool>> _relaunch;
    readonly bool _canRenameOnPlatform;
    readonly Task _startupSettled;
    readonly bool _nameOverridden;
    readonly Func<MutationRequest, CancellationToken, Task<bool>> _canRetire;
    readonly NotificationSettingsService? _notificationSettings;
    readonly IDesktopNotificationAccess? _notificationAccess;
    readonly CancellationTokenSource _lifetime;
    readonly CompositeDisposable _subscriptions = new();
    AttachStatus _status = new(AttachState.Connecting, null, null);
    DaemonStatusDto? _snapshot;
    int _savedCapacity;
    string _name;
    decimal? _capacity;
    bool _isBusy;
    bool _windowsServiceVisible;
    bool _windowsServiceOffered;
    string? _windowsServiceMessage;
    bool _needsAppRestart;
    bool _notifyOnPermissions = true;
    bool _notifyOnQuestions = true;
    bool _notifyOnIdle = true;
    bool _autoResume;
    readonly SemaphoreSlim _autoResumeSaves = new(1, 1);
    string? _autoResumeMessage;
    string? _message;
    string? _notificationMessage;
    DesktopNotificationAccess _access;
    int _accessReads;
    int _accessShown;

    public SettingsViewModel(
            SettingsProfileStore settings, IDaemonClientService service, ILocalControlOps ops,
            Func<string, CancellationToken, Task<bool>> targetRunning,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<LifecyclePrompt, CancellationToken, Task<bool>> confirm,
            Func<CancellationToken, Task<bool>> relaunch, bool canRenameOnPlatform,
            Task startupSettled, Func<MutationRequest, CancellationToken, Task<bool>> canRetire,
            bool nameOverridden = false, bool needsAppRestart = false, CancellationToken appLifetime = default,
            NotificationSettingsService? notificationSettings = null,
            IDesktopNotificationAccess? notificationAccess = null, ProfilesSettingsViewModel? profiles = null,
            Func<CancellationToken, Task<MutationOutcome?>>? moveToWindowsService = null,
            Func<CancellationToken, Task<ServiceSnapshot?>>? serviceStatus = null,
            HarnessesSettingsViewModel? harnesses = null, CaptureSettingsViewModel? capture = null) {
        Capture = capture ?? new CaptureSettingsViewModel(settings, appLifetime);
        Harnesses = harnesses;
        _settings = settings;
        _ops = ops;
        _runningName = service.DaemonName;
        _targetRunning = targetRunning;
        _runMutation = runMutation;
        _moveToWindowsService = moveToWindowsService;
        _confirm = confirm;
        _relaunch = relaunch;
        _canRenameOnPlatform = canRenameOnPlatform;
        _startupSettled = startupSettled;
        _canRetire = canRetire;
        _notificationSettings = notificationSettings;
        _notificationAccess = notificationAccess;
        Profiles = profiles;
        _nameOverridden = nameOverridden;
        _needsAppRestart = needsAppRestart;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);

        var saved = settings.Load();
        _name = saved.Name ?? service.DaemonName;
        _capacity = _savedCapacity = saved.MaxAgents;
        _autoResume = saved.AutoResume ?? true;

        Observable.FromAsync(startupSettled.WaitAsync).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => Refresh(), _ => Refresh()).DisposeWith(_subscriptions);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave)).DisposeWith(_subscriptions);
        RenameCommand = ReactiveCommand.CreateFromTask(RenameAsync, this.WhenAnyValue(x => x.CanRename)).DisposeWith(_subscriptions);
        NotificationAccessCommand = ReactiveCommand.CreateFromTask(ResolveNotificationAccessAsync).DisposeWith(_subscriptions);
        WindowsServiceCommand = ReactiveCommand.CreateFromTask(InstallWindowsServiceAsync,
            this.WhenAnyValue(x => x.IsBusy, x => x.WindowsServiceOffered, (busy, offered) => !busy && offered)).DisposeWith(_subscriptions);
        if (serviceStatus is not null && moveToWindowsService is not null && OperatingSystem.IsWindows())
            _ = LoadWindowsServiceOfferAsync(serviceStatus);
        service.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
            _status = status;
            if (status.State != AttachState.Connected) _snapshot = null;
            Refresh();
        }).DisposeWith(_subscriptions);
        service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(snapshot => {
            _snapshot = snapshot;
            Refresh();
        }).DisposeWith(_subscriptions);
        if (notificationSettings is not null) {
            ApplyNotificationPreferences(notificationSettings.Current);
            notificationSettings.Changes.Skip(1).ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ApplyNotificationPreferences).DisposeWith(_subscriptions);
        }
        Capture.WhenAnyValue(x => x.Busy).Subscribe(_ => Refresh()).DisposeWith(_subscriptions);
        if (Harnesses is not null)
            Harnesses.WhenAnyValue(x => x.Busy).Subscribe(_ => Refresh()).DisposeWith(_subscriptions);
        RefreshNotificationAccess();
    }

    public string Name {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); Refresh(); }
    }

    public decimal? Capacity {
        get => _capacity;
        set { this.RaiseAndSetIfChanged(ref _capacity, value); Refresh(); }
    }

    /// <c>daemon.auto_resume</c>: whether a starting daemon brings back the agents a restart stopped.
    /// Saved on change; the daemon reads it only when it starts.
    public bool AutoResume {
        get => _autoResume;
        set {
            if (value == _autoResume) return;
            this.RaiseAndSetIfChanged(ref _autoResume, value);
            _ = SaveAutoResumeAsync();
        }
    }

    public string? AutoResumeMessage {
        get => _autoResumeMessage;
        private set => this.RaiseAndSetIfChanged(ref _autoResumeMessage, value);
    }

    // Saves run one at a time and each writes the selection current when it starts, so the last one
    // to finish holds the latest selection whatever order the toggles' writes were scheduled in. A
    // save outlives the window: closing Settings right after a toggle must not drop the choice.
    async Task SaveAutoResumeAsync() {
        string? message;
        await _autoResumeSaves.WaitAsync(CancellationToken.None);
        try {
            var value = Volatile.Read(ref _autoResume);
            await _settings.SaveAutoResumeAsync(value, CancellationToken.None);
            message = value == Volatile.Read(ref _autoResume) ? "Saved. The daemon reads this when it starts." : null;
        } catch (Exception ex) {
            message = $"Could not save the setting: {ex.Message}";
        } finally {
            _autoResumeSaves.Release();
        }
        if (message is not null && !_lifetime.IsCancellationRequested) AutoResumeMessage = message;
    }

    public bool IsBusy {
        get => _isBusy;
        private set { this.RaiseAndSetIfChanged(ref _isBusy, value); Refresh(); }
    }

    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public string? NotificationMessage {
        get => _notificationMessage;
        private set => this.RaiseAndSetIfChanged(ref _notificationMessage, value);
    }

    public bool CanManageNotifications => _notificationSettings is not null;

    public ProfilesSettingsViewModel? Profiles { get; }
    public string ProfileName => _settings.ProfileName;
    public string ProfileCardLabel => Profiles is null ? "Management unavailable" : "Manage profiles";
    public string ProfileCardHint => $"Current profile: {ProfileName}\n"
        + (Profiles is null ? "Profile management is unavailable" : "Select to manage profiles");
    public string ServerLabel => Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var url) ? url.Authority : _settings.ServerUrl;
    public CaptureSettingsViewModel Capture { get; }
    public HarnessesSettingsViewModel? Harnesses { get; }
    public bool HasActiveOperation => IsBusy || Capture.Busy || Harnesses?.Busy == true;

    public AccountsSettingsViewModel? Accounts { get; init; }
    public AppearanceSettingsViewModel? Appearance { get; init; }
    public PrivacySettingsViewModel? Privacy { get; init; }
    /// <summary>The local server's search, when this app runs one. The app owns it, so closing Settings leaves it running.</summary>
    public LocalSearchViewModel? Search { get; init; }
    public ExperimentalSettingsViewModel? Experimental { get; init; }
    public AboutSettingsViewModel? About { get; init; }

    /// What the OS calls the app that holds its notification switches.
    internal static string SystemSettingsName { get; } = OperatingSystem.IsWindows() ? "Windows Settings" : "System Settings";

    public string? NotificationAccessText => _access switch {
        DesktopNotificationAccess.Denied =>
            $"Notifications from kcap are turned off in {SystemSettingsName}, so none of these will appear.",
        DesktopNotificationAccess.NotDetermined =>
            "kcap has not been allowed to send notifications yet.",
        _ => null,
    };

    public string? NotificationAccessAction => _access switch {
        DesktopNotificationAccess.Denied => $"Open {SystemSettingsName}",
        DesktopNotificationAccess.NotDetermined => "Allow notifications",
        _ => null,
    };

    public bool NotifyOnPermissions {
        get => _notifyOnPermissions;
        set {
            if (!CanManageNotifications || value == _notifyOnPermissions) return;
            this.RaiseAndSetIfChanged(ref _notifyOnPermissions, value);
            PersistNotificationPreferences();
        }
    }

    public bool NotifyOnQuestions {
        get => _notifyOnQuestions;
        set {
            if (!CanManageNotifications || value == _notifyOnQuestions) return;
            this.RaiseAndSetIfChanged(ref _notifyOnQuestions, value);
            PersistNotificationPreferences();
        }
    }

    public bool NotifyOnIdle {
        get => _notifyOnIdle;
        set {
            if (!CanManageNotifications || value == _notifyOnIdle) return;
            this.RaiseAndSetIfChanged(ref _notifyOnIdle, value);
            PersistNotificationPreferences();
        }
    }

    public bool CanEdit => !IsBusy && !Capture.Busy && Harnesses?.Busy != true && !_needsAppRestart;
    public bool CanSave => CanEdit && CapacityError is null && Capacity != _savedCapacity;
    public bool CanRename => CanEdit && _canRenameOnPlatform && !_nameOverridden && _startupSettled.IsCompletedSuccessfully &&
        NameError is null && Name != DaemonStore.Sanitize(_runningName) && Idle;
    bool Idle => _status.State == AttachState.Unreachable ||
        (_status.State == AttachState.Connected && _snapshot?.Daemon.ActiveAgents == 0);

    public string? NameError => string.IsNullOrWhiteSpace(Name) || DaemonStore.Sanitize(Name) != Name
        ? "Use lowercase letters, numbers, dots, hyphens or underscores, with no surrounding spaces or repeated hyphens." : null;
    public string? CapacityError => Capacity is not { } value || value < 0 || value > int.MaxValue || decimal.Truncate(value) != value
        ? "Enter a whole number. 0 means no limit." : null;
    public string? RenameHint => !_canRenameOnPlatform ? "Renaming is available on macOS and Windows."
        : _needsAppRestart ? "Restart this app to manage the renamed daemon."
        : _nameOverridden ? "The name is set by KCAP_DAEMON_NAME. Remove that environment override and restart the app before renaming."
        : !_startupSettled.IsCompletedSuccessfully ? "Waiting for daemon startup to finish…"
        : Name == DaemonStore.Sanitize(_runningName) ? "The running daemon already uses this service ID. Change the name to rename it."
        : _status.State == AttachState.Connected && _snapshot?.Daemon.ActiveAgents > 0
            ? $"Wait for the {_snapshot.Daemon.ActiveAgents} active agents to finish before renaming."
            : !Idle ? "Waiting for the daemon’s current agent count…" : "Renaming restarts the daemon and relaunches this app.";

    public string StatusLabel => _status.State switch {
        AttachState.Connected when _snapshot is { Daemon.MaxAgents: 0 } snap =>
            $"{snap.Daemon.ActiveAgents} · unlimited",
        AttachState.Connected when _snapshot is { } snap =>
            $"{snap.Daemon.ActiveAgents} / {snap.Daemon.MaxAgents}",
        AttachState.Unreachable => "Not running",
        _ => "Connecting",
    };

    public bool StatusShowsAgents => _status.State == AttachState.Connected && _snapshot is not null;

    /// Whether the daemon runs and this app is attached to it.
    public string DaemonState => DaemonStatusText.Attach(_status, _snapshot?.Daemon);
    public string? DaemonStateHint => DaemonStatusText.AttachHint(_status);

    /// The daemon's own connection to its server, apart from whether it runs; null while it is not attached.
    public string? ServerState => DaemonStatusText.Server(_status, _snapshot?.Daemon);
    public string? ServerStateHint => DaemonStatusText.ServerHint(_status, _snapshot?.Daemon);

    public string StatusTip => _status.State switch {
        AttachState.Connected when _snapshot is { } snap =>
            snap.Daemon.MaxAgents == 0
                ? $"Running as {snap.Daemon.Name}. No capacity limit."
                : $"Running as {snap.Daemon.Name}.",
        AttachState.Unreachable => "Changes apply when the daemon starts.",
        _ => "Connecting to the daemon…",
    };

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameCommand { get; }
    public ReactiveCommand<Unit, Unit> NotificationAccessCommand { get; }
    public ReactiveCommand<Unit, Unit> WindowsServiceCommand { get; }

    /// The card shows once the service was found installable and stays for the outcome line.
    public bool WindowsServiceVisible {
        get => _windowsServiceVisible;
        private set => this.RaiseAndSetIfChanged(ref _windowsServiceVisible, value);
    }

    /// A Scheduled Task supervises the daemon and this install carries the Windows service: the way back to
    /// the service after its one-time offer was declined.
    public bool WindowsServiceOffered {
        get => _windowsServiceOffered;
        private set => this.RaiseAndSetIfChanged(ref _windowsServiceOffered, value);
    }

    public string? WindowsServiceMessage {
        get => _windowsServiceMessage;
        private set => this.RaiseAndSetIfChanged(ref _windowsServiceMessage, value);
    }

    async Task LoadWindowsServiceOfferAsync(Func<CancellationToken, Task<ServiceSnapshot?>> serviceStatus) {
        ServiceSnapshot? snap;
        try {
            snap = await serviceStatus(_lifetime.Token);
        } catch (Exception) {
            return;
        }
        if (snap is not { Supervisor: "windows_scheduled_task", WindowsServiceInstallable: true }) return;
        RxSchedulers.MainThreadScheduler.Schedule(() => {
            WindowsServiceOffered = true;
            WindowsServiceVisible = true;
        });
    }

    async Task InstallWindowsServiceAsync() {
        IsBusy = true;
        WindowsServiceMessage = null;
        try {
            var resumes = _status.Capabilities?.Contains(DaemonLifecycleController.ResumeCapability) == true;
            var disclosure = DaemonLifecycleController.WindowsServiceDisclosure(_snapshot?.Daemon.ActiveAgents ?? 0, resumes);
            if (!await _confirm(new LifecyclePrompt(LifecyclePrompt.KindWindowsService, null, null, false, disclosure), _lifetime.Token)) return;
            WindowsServiceMessage = "Waiting for administrator approval…";
            var outcome = await _moveToWindowsService!(_lifetime.Token);
            WindowsServiceMessage = outcome switch {
                MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => "The daemon now runs under the kcap Windows service.",
                MutationOutcome.Refused { Reason: "elevation_declined" } => "The administrator prompt was declined; nothing changed.",
                MutationOutcome.UnconfirmedNoAttach =>
                    "The administrator prompt was not answered in time, so nothing changed. Run it again from here when you can approve it.",
                null => "Nothing changed. Restart the app and try again.",
                _ => "The Windows service could not take the daemon over; it keeps its Scheduled Task.",
            };
            if (outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout) WindowsServiceOffered = false;
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            WindowsServiceMessage = $"Could not install the Windows service: {ex.Message}";
        } finally { IsBusy = false; }
    }

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
                    "Only the saved capacity is used. Save any capacity change first."), _lifetime.Token)) return;
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
                Message = "Update kcap before renaming. This version could not confirm it can retire the old service. The saved name is unchanged.";
                return;
            }
            if (!_startupSettled.IsCompletedSuccessfully || !Idle) {
                Message = "Wait until startup has finished and the daemon is idle before renaming.";
                return;
            }
            var previous = await _settings.SaveNameAsync(name, _lifetime.Token);
            Message = "Name saved. Restarting the daemon…";
            var outcome = await _runMutation(request!, _lifetime.Token);
            if (outcome is MutationOutcome.Failed { Reason: { } refusal } && RenameRefusal.ChangedNothing(outcome)) {
                await _settings.RestoreNameAsync(name, previous, _lifetime.Token);
                Message = RenameRefusal.Message(refusal, name);
                return;
            }
            if (outcome is MutationOutcome.Failed { Reason: "cli_unsupported" }) {
                await _settings.RestoreNameAsync(name, previous, _lifetime.Token);
                Message = "This version of kcap cannot rename the daemon. Update kcap and try again. The saved name was restored unless another edit changed it.";
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

    /// Access changes in System Settings, outside the app, so the window re-reads it on activation.
    public void RefreshNotificationAccess() => _ = ReadNotificationAccessAsync();

    async Task ResolveNotificationAccessAsync() {
        if (_notificationAccess is null) return;
        if (_access == DesktopNotificationAccess.Denied) {
            _notificationAccess.OpenSystemSettings();
            return;
        }
        if (_access != DesktopNotificationAccess.NotDetermined) return;
        try {
            await _notificationAccess.RequestAsync();
        } catch {
        }
        await ReadNotificationAccessAsync();
    }

    // Reads overlap (opening, every activation, after a request) and answer in any order, so only
    // an answer to a later read than the one on screen is applied. A request is followed by a read
    // of its own rather than applied directly: its prompt can stay open across many activations.
    async Task ReadNotificationAccessAsync() {
        if (_notificationAccess is null) return;
        var read = ++_accessReads;
        DesktopNotificationAccess access;
        try {
            access = await _notificationAccess.GetAsync();
        } catch {
            return;
        }
        if (_lifetime.IsCancellationRequested || read < _accessShown) return;
        _accessShown = read;
        if (access == _access) return;
        _access = access;
        this.RaisePropertyChanged(nameof(NotificationAccessText));
        this.RaisePropertyChanged(nameof(NotificationAccessAction));
    }

    void ApplyNotificationPreferences(NotificationPreferences preferences) {
        this.RaiseAndSetIfChanged(ref _notifyOnPermissions, preferences.Permissions, nameof(NotifyOnPermissions));
        this.RaiseAndSetIfChanged(ref _notifyOnQuestions, preferences.Questions, nameof(NotifyOnQuestions));
        this.RaiseAndSetIfChanged(ref _notifyOnIdle, preferences.Idle, nameof(NotifyOnIdle));
    }

    void PersistNotificationPreferences() {
        var preferences = new NotificationPreferences(_notifyOnPermissions, _notifyOnQuestions, _notifyOnIdle);
        _ = PersistNotificationPreferencesAsync(preferences);
    }

    async Task PersistNotificationPreferencesAsync(NotificationPreferences preferences) {
        if (_notificationSettings is null) return;
        var saved = false;
        try {
            saved = await _notificationSettings.SaveAsync(preferences);
        } catch {
        }
        if (_lifetime.IsCancellationRequested) return;
        NotificationMessage = saved ? null : "Could not save notification settings. The change still applies for this run.";
    }

    void Refresh() {
        this.RaisePropertyChanged(nameof(HasActiveOperation));
        this.RaisePropertyChanged(nameof(CanEdit));
        this.RaisePropertyChanged(nameof(CanSave));
        this.RaisePropertyChanged(nameof(CanRename));
        this.RaisePropertyChanged(nameof(NameError));
        this.RaisePropertyChanged(nameof(CapacityError));
        this.RaisePropertyChanged(nameof(RenameHint));
        this.RaisePropertyChanged(nameof(StatusLabel));
        this.RaisePropertyChanged(nameof(StatusShowsAgents));
        this.RaisePropertyChanged(nameof(StatusTip));
        this.RaisePropertyChanged(nameof(DaemonState));
        this.RaisePropertyChanged(nameof(DaemonStateHint));
        this.RaisePropertyChanged(nameof(ServerState));
        this.RaisePropertyChanged(nameof(ServerStateHint));
    }

    public void Dispose() {
        _lifetime.Cancel();
        Capture.Dispose();
        Harnesses?.Dispose();
        Profiles?.Dispose();
        _subscriptions.Dispose();
        Accounts?.Dispose();
        Appearance?.Dispose();
        Privacy?.Dispose();
        Experimental?.Dispose();
        About?.Dispose();
        _lifetime.Dispose();
    }
}
