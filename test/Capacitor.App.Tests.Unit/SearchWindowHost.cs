using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// A shown MainWindow with a rail and a search page whose requests are recorded and answered with
/// Response. The debounce and the page's own scheduling run on Scheduler.
sealed class SearchWindowHost : IDisposable {
    public FakeDaemonClientService Service { get; } = new();
    public FakeAgentDirectory Directory { get; } = new();
    public FakeServerSessionsService Sessions { get; } = new();
    public HistoricalScheduler Scheduler { get; } = new();
    public List<SearchRequest> Requests { get; } = [];
    public SearchResponseDto Response { get; set; } = new();
    public ServerReadOutcome Outcome { get; set; } = ServerReadOutcome.Ok;
    public System.Reactive.Subjects.BehaviorSubject<ServerLaneStatus> Lane { get; }
    public SessionRailViewModel Rail { get; }
    public MainWindowViewModel Vm { get; }
    public MainWindow Window { get; }
    public int SignIns { get; private set; }

    public SearchWindowHost(bool signedOut = false, Func<string, WorkspaceViewModel>? workspaceFactory = null) {
        Lane = new(new ServerLaneStatus(signedOut ? ServerLaneState.SignedOut : ServerLaneState.Connected));
        Rail = new SessionRailViewModel(Directory, _ => { }, _ => { }, TimeProvider.System, sessions: Sessions);
        Vm = new MainWindowViewModel(
            Service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            workspaceFactory: workspaceFactory, rail: Rail,
            laneStatus: Lane,
            requestSignIn: () => SignIns++,
            searchFactory: open => new SessionSearchViewModel(
                (request, _) => {
                    Requests.Add(request);
                    return Task.FromResult(new SearchFetch(Outcome, Outcome == ServerReadOutcome.Ok ? Response : null));
                },
                Sessions.Identity, Observable.Never<string>(), new FakeTimeProvider(), Scheduler, open),
            sessions: Sessions);
        Window = new MainWindow { DataContext = Vm };
        Window.Show();
        Settle();
    }

    public void Settle() {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// Past the debounce, and the answer applied.
    public void SettleSearch() {
        Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(151));
        Settle();
    }

    public T Named<T>(string name) where T : Control =>
        Window.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    public IInputElement? Focused => Window.FocusManager?.GetFocusedElement();

    public void Dispose() {
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        Vm.CloseWorkspace();
        Vm.Search?.Dispose();
        Rail.Dispose();
        Sessions.Dispose();
        Directory.Dispose();
    }
}
