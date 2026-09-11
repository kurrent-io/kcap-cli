using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Prototypes;

// Throwaway material comparison in the real desktop shell. All session data and actions are
// in memory; the normal app startup, daemon connection, updater and tray are never started.
sealed class LiquidGlassPrototypeApp : App {
    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var data = new PrototypeData();
        var home = new HomeViewModel(data, data, data,
            () => Task.FromResult(new[] { PrototypeData.Repo, "/workspace/kcap-server" })) {
            SelectedRepoPath = PrototypeData.Repo,
        };
        var activity = new ActivityViewModel(() => new ConsentLogReadResult([], true), () => "prototype", data);
        var rail = new SessionRailViewModel(data, _ => { }, _ => { }, path => path);
        var model = new MainWindowViewModel(data, CancellationToken.None, activity,
            home: home, rail: rail, tenantName: "Design preview");
        var window = new MainWindow {
            DataContext = model,
            Title = "Capacitor · Liquid glass prototype",
            Width = 1320,
            Height = 820,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Loaded += (_, _) => GlassLauncherPrototype.Attach(window);
        window.Closed += (_, _) => {
            home.Dispose();
            activity.Dispose();
            rail.Dispose();
            data.Dispose();
        };
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.MainWindow = window;
    }
}

sealed class PrototypeData : IDaemonClientService, IAppStateStore, ILaunchClient, IAgentDirectory, ITicker, IDisposable {
    public const string Repo = "/workspace/kcap-cli";
    readonly SourceCache<AgentRow, string> _rows = new(row => row.Key);
    AppState _state = new();

    public PrototypeData() {
        Add("glass", "Explore liquid glass", "codex", Repo, false);
        Add("rail", "Polish the session rail", "claude", Repo, true);
        Add("stream", "Improve streaming responses", "codex", "/workspace/kcap-server", false);
    }

    void Add(string id, string title, string vendor, string repo, bool waiting) =>
        _rows.AddOrUpdate(new AgentRow(
            $"local:{id}", AgentOrigin.Local, id, "agent", vendor, "Running", DateTime.UtcNow,
            repo, title, null, null, null, null, null, null,
            repo, Path.GetFileName(repo), repo, "main", waiting));

    public IObservable<AttachStatus> Status => Observable.Return(new AttachStatus(AttachState.Connected, null, null));
    public IObservable<DaemonStatusDto> Snapshots => Observable.Return(new DaemonStatusDto(
        new DaemonInfoDto("Preview", "0.0.0", "https://preview.invalid", "connected", 5, 3,
            SupportedVendors: ["claude", "codex", "cursor"]), []));
    public SourceCache<AgentStatusDto, string> Agents { get; } = new(agent => agent.Id);
    public string DaemonName => "Preview";
    public IObservableCache<AgentRow, string> Rows => _rows;
    public IObservable<bool> RemoteStale => Observable.Return(false);
    public IObservable<long> Ticks => Observable.Never<long>();
    public Task RestartLoopAsync() => Task.CompletedTask;
    public Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) => Task.FromResult(new StartDaemonResult(true, null));
    public Task<AppState> LoadAsync() => Task.FromResult(_state);
    public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
        _state = mutate(_state);
        return Task.FromResult(true);
    }
    public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
        Task.FromResult(new LaunchOutcome(false, null, "Preview only · your goal stays here; no session was launched."));
    public void Dispose() {
        _rows.Dispose();
        Agents.Dispose();
    }
}
