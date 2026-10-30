using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// What is installed on this computer and how to update it, in one place a bug report can copy from.
public sealed class AboutSettingsViewModel : ReactiveObject, IDisposable {
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime;
    readonly Func<CancellationToken, Task<string?>> _cliVersion;
    string? _daemonVersion;
    string? _cliVersionText;
    UpdateMenuItem? _update;
    readonly Func<bool, Task>? _setBetaUpdates;
    bool _betaUpdates;

    public AboutSettingsViewModel(IDaemonClientService service, Func<CancellationToken, Task<string?>> cliVersion,
            LocalStackViewModel? localStack = null, IObservable<UpdateMenuItem>? updateMenu = null, Func<Task>? updateAction = null,
            IUrlOpener? opener = null, Func<bool>? followsBetas = null, Func<bool, Task>? setBetaUpdates = null,
            CancellationToken appLifetime = default) {
        _cliVersion = cliVersion;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        LocalStack = localStack;
        _setBetaUpdates = setBetaUpdates;
        _betaUpdates = followsBetas?.Invoke() ?? false;
        // The service keeps its last snapshot after a disconnect, so the version only stands while attached.
        service.Status.CombineLatest(service.Snapshots.Select(s => MainWindowViewModel.StripBuildMetadata(s.Daemon.Version)),
                (status, version) => status.State == AttachState.Connected ? version : "")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(v => DaemonVersion = v.Length > 0 ? v : null)
            .DisposeWith(_subscriptions);
        service.Status.CombineLatest(service.Snapshots.Select(s => (DaemonInfoDto?)s.Daemon).StartWith((DaemonInfoDto?)null),
                (status, daemon) => (status, daemon))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(t => {
                DaemonStatus = DaemonStatusFor(t.status, t.daemon);
                var attached = t.status.State == AttachState.Connected ? t.daemon : null;
                ServerConnection = attached is null ? null
                    : string.IsNullOrEmpty(attached.ServerUrl) ? attached.Connection
                    : $"{attached.Connection} · {attached.ServerUrl}";
                RunningAgents = attached is null ? null
                    : attached.MaxAgents == 0 ? $"{attached.ActiveAgents} running · no limit"
                    : $"{attached.ActiveAgents} running · limit {attached.MaxAgents}";
            })
            .DisposeWith(_subscriptions);
        updateMenu?.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(item => Update = item)
            .DisposeWith(_subscriptions);
        var links = opener ?? new ShellUrlOpener();
        UpdateCommand = ReactiveCommand.CreateFromTask(updateAction ?? (() => Task.CompletedTask)).DisposeWith(_subscriptions);
        OpenDocsCommand = ReactiveCommand.Create(() => LinkPolicy.Open(links, AppMenuBar.DocsUrl)).DisposeWith(_subscriptions);
        OpenReleaseNotesCommand = ReactiveCommand.Create(() => LinkPolicy.Open(links, AppMenuBar.ChangelogUrl)).DisposeWith(_subscriptions);
    }

    public string AppVersion => Services.AppVersion.Display;
    public LocalStackViewModel? LocalStack { get; }

    public string? DaemonVersion {
        get => _daemonVersion;
        private set {
            this.RaiseAndSetIfChanged(ref _daemonVersion, value);
            this.RaisePropertyChanged(nameof(VersionReport));
        }
    }

    /// The attach to this machine's daemon, with its name while attached.
    public string DaemonStatus {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "connecting";

    /// The daemon's own connection to its server, and which server; null while not attached.
    public string? ServerConnection {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// Agents running on the daemon against its configured limit; null while not attached.
    public string? RunningAgents {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    internal static string DaemonStatusFor(AttachStatus status, DaemonInfoDto? daemon) => status.State switch {
        AttachState.Connected   => daemon?.Name is { Length: > 0 } name ? $"connected · {name}" : "connected",
        AttachState.Connecting  => "connecting",
        _ => status.Reason switch {
            "daemon_unreachable"         => "not running",
            "daemon_incompatible"        => "version doesn't match the app",
            ExpectedDaemonRestart.Reason => "restarting",
            _                            => "unreachable",
        },
    };

    public string? CliVersion {
        get => _cliVersionText;
        private set {
            this.RaiseAndSetIfChanged(ref _cliVersionText, value);
            this.RaisePropertyChanged(nameof(VersionReport));
        }
    }

    public UpdateMenuItem? Update {
        get => _update;
        private set {
            this.RaiseAndSetIfChanged(ref _update, value);
            this.RaisePropertyChanged(nameof(UpdateVisible));
            this.RaisePropertyChanged(nameof(BetaUpdatesVisible));
        }
    }

    public bool UpdateVisible => Update is { Visible: true };

    public bool BetaUpdatesVisible => UpdateVisible && _setBetaUpdates is not null;

    public bool BetaUpdates {
        get => _betaUpdates;
        set {
            if (_setBetaUpdates is null || value == _betaUpdates) return;
            this.RaiseAndSetIfChanged(ref _betaUpdates, value);
            _ = _setBetaUpdates(value);
        }
    }

    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDocsCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenReleaseNotesCommand { get; }

    /// The lines a bug report needs, in the order this page shows them.
    public string VersionReport => string.Join('\n', new[] {
        $"Desktop app: {AppVersion}",
        $"Command line: {CliVersion ?? "unknown"}",
        $"Daemon: {DaemonVersion ?? "not connected"}",
        LocalStack?.Version is { } local ? $"Local server: {local}" : null,
        $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})",
    }.OfType<string>());

    /// The command line answers by running, so it is read once the page opens rather than at construction.
    public async Task LoadAsync() {
        try {
            var raw = await _cliVersion(_lifetime.Token);
            CliVersion = raw is { Length: > 0 } ? MainWindowViewModel.StripBuildMetadata(raw) : "not found";
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception) {
            CliVersion = "not found";
        }
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}
