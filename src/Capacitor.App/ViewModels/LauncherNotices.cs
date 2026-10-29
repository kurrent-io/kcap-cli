using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Which launcher notices show. Only the most blocking problem shows until the user asks for the rest:
/// the local stack first, since nothing connects without it, then the local server, then the daemon and
/// server connection. A local server card is folded into a local stack problem, whose card already
/// offers the fix.
public sealed class LauncherNotices : ReactiveObject, IDisposable {
    readonly BehaviorSubject<(bool Visible, bool Note)> _stack = new((false, false));
    readonly BehaviorSubject<bool> _server = new(false);
    readonly BehaviorSubject<bool> _connection = new(false);
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
            server = server && !(stack.Visible && !stack.Note);
            bool[] problems = [stack.Visible, server, connection];
            var count = problems.Count(p => p);
            var first = Array.IndexOf(problems, true);
            bool Show(int i) => problems[i] && (expanded || i == first);
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
        // Back to one card once only one problem is left, so the next pile of problems starts folded.
        shown.Where(s => !s.More && _expanded.Value).Subscribe(_ => _expanded.OnNext(false)).DisposeWith(_disposables);
        _expandedProperty = shown.Select(s => s.More && _expanded.Value).ToProperty(this, x => x.Expanded).DisposeWith(_disposables);

        ToggleCommand = ReactiveCommand.Create(() => _expanded.OnNext(!_expanded.Value));
    }

    public bool ShowStack => _showStack.Value;
    public bool ShowServer => _showServer.Value;
    public bool ShowConnection => _showConnection.Value;

    /// Problems folded behind the top one; zero once expanded.
    public int HiddenCount => _hiddenCount.Value;

    /// The toggle's text, or null when there is nothing to fold or unfold.
    public string? ToggleLabel => _toggleLabel.Value;

    /// True while every problem shows and there is more than one to show.
    public bool Expanded => _expandedProperty.Value;

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public void Track(IObservable<(bool Visible, bool Note)> stack) => stack.Subscribe(_stack.OnNext).DisposeWith(_disposables);
    public void TrackServer(IObservable<bool> server) => server.Subscribe(_server.OnNext).DisposeWith(_disposables);
    public void TrackConnection(IObservable<bool> connection) => connection.Subscribe(_connection.OnNext).DisposeWith(_disposables);

    public void Dispose() {
        _disposables.Dispose();
        _stack.Dispose();
        _server.Dispose();
        _connection.Dispose();
        _expanded.Dispose();
    }
}
