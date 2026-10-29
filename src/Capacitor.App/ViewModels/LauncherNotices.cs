using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Which launcher notices show. One card shows until the user asks for the rest: a problem they can act
/// on before anything still in progress, and that before a note. Among equals the local stack leads, since
/// nothing connects without it, then the local server, then the daemon and server connection. A local
/// server card folds into a local stack card that is not a note, which already covers the server.
public sealed class LauncherNotices : ReactiveObject, IDisposable {
    public enum Kind { Problem, Progress, Note }

    readonly BehaviorSubject<(bool Visible, Kind Kind)> _stack = new((false, Kind.Note));
    readonly BehaviorSubject<(bool Visible, Kind Kind)> _server = new((false, Kind.Problem));
    readonly BehaviorSubject<(bool Visible, Kind Kind)> _connection = new((false, Kind.Problem));
    readonly BehaviorSubject<bool> _expanded = new(false);
    readonly CompositeDisposable _disposables = new();

    readonly ObservableAsPropertyHelper<bool> _showStack;
    readonly ObservableAsPropertyHelper<bool> _showServer;
    readonly ObservableAsPropertyHelper<bool> _showConnection;
    readonly ObservableAsPropertyHelper<int> _hiddenCount;
    readonly ObservableAsPropertyHelper<string?> _toggleLabel;
    readonly ObservableAsPropertyHelper<bool> _expandedProperty;

    public LauncherNotices() {
        var shown = _stack.CombineLatest(_server, _connection, _expanded, (stack, server, connection, expanded) => {
            if (stack.Visible && stack.Kind != Kind.Note) server = server with { Visible = false };
            (bool Visible, Kind Kind)[] cards = [stack, server, connection];
            var count = cards.Count(c => c.Visible);
            var top = Enumerable.Range(0, cards.Length).Where(i => cards[i].Visible)
                .OrderBy(i => cards[i].Kind).ThenBy(i => i).DefaultIfEmpty(-1).First();
            bool Show(int i) => cards[i].Visible && (expanded || i == top);
            var hidden = expanded ? 0 : Math.Max(count - 1, 0);
            var label = hidden > 0 ? $"{hidden} more {(hidden == 1 ? "issue" : "issues")}"
                : expanded && count > 1 ? "Show fewer" : null;
            return (Stack: Show(0), Server: Show(1), Connection: Show(2), Hidden: hidden, More: count > 1, Label: label);
        }).Publish().RefCount();

        _showStack = shown.Select(s => s.Stack).ToProperty(this, x => x.ShowStack).DisposeWith(_disposables);
        _showServer = shown.Select(s => s.Server).ToProperty(this, x => x.ShowServer).DisposeWith(_disposables);
        _showConnection = shown.Select(s => s.Connection).ToProperty(this, x => x.ShowConnection).DisposeWith(_disposables);
        _hiddenCount = shown.Select(s => s.Hidden).ToProperty(this, x => x.HiddenCount).DisposeWith(_disposables);
        _toggleLabel = shown.Select(s => s.Label).ToProperty(this, x => x.ToggleLabel).DisposeWith(_disposables);
        // Back to one card once only one is left, so the next pile starts folded.
        shown.Where(s => !s.More && _expanded.Value).Subscribe(_ => _expanded.OnNext(false)).DisposeWith(_disposables);
        _expandedProperty = shown.Select(s => s.More && _expanded.Value).ToProperty(this, x => x.Expanded).DisposeWith(_disposables);

        ToggleCommand = ReactiveCommand.Create(() => _expanded.OnNext(!_expanded.Value));
    }

    public bool ShowStack => _showStack.Value;
    public bool ShowServer => _showServer.Value;
    public bool ShowConnection => _showConnection.Value;

    /// Cards folded behind the top one; zero once expanded.
    public int HiddenCount => _hiddenCount.Value;

    /// The toggle's text, or null when there is nothing to fold or unfold.
    public string? ToggleLabel => _toggleLabel.Value;

    /// True while every card shows and there is more than one to show.
    public bool Expanded => _expandedProperty.Value;

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public void TrackStack(IObservable<(bool Visible, Kind Kind)> stack) => stack.Subscribe(_stack.OnNext).DisposeWith(_disposables);
    public void TrackServer(IObservable<(bool Visible, Kind Kind)> server) => server.Subscribe(_server.OnNext).DisposeWith(_disposables);
    public void TrackConnection(IObservable<(bool Visible, Kind Kind)> connection) => connection.Subscribe(_connection.OnNext).DisposeWith(_disposables);

    public void Dispose() {
        _disposables.Dispose();
        _stack.Dispose();
        _server.Dispose();
        _connection.Dispose();
        _expanded.Dispose();
    }
}
