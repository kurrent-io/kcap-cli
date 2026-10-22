using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Holds a still of the content pane over a session switch until the incoming chat has laid out
/// its first read. A new workspace fills in over several Background-priority dispatches, each run
/// after a render, so without the still every half-built state is painted in turn.
public sealed class WorkspaceSwitchCover {
    static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(400);

    readonly Control _pane;
    readonly Control _cover;
    readonly StillImage _still;
    readonly SerialDisposable _hold = new();
    int _generation;

    public WorkspaceSwitchCover(Control pane, Control cover, StillImage still) {
        _pane = pane;
        _cover = cover;
        _still = still;
    }

    public IDisposable Track(MainWindowViewModel vm) {
        void OnChanging(object? sender, PropertyChangingEventArgs e) {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentWorkspace)) Capture();
        }
        vm.PropertyChanging += OnChanging;
        var swaps = vm.WhenAnyValue(x => x.CurrentWorkspace).Skip(1).Subscribe(workspace => Hold(workspace, vm.Time));
        return Disposable.Create(() => {
            vm.PropertyChanging -= OnChanging;
            swaps.Dispose();
            Lift();
        });
    }

    void Capture() {
        if (_cover.IsVisible) return;
        var size = _pane.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        var scale = TopLevel.GetTopLevel(_pane)?.RenderScaling ?? 1;
        var still = new RenderTargetBitmap(PixelSize.FromSize(size, scale), new Vector(96 * scale, 96 * scale));
        still.Render(_pane);
        _still.Source = still;
        _cover.IsVisible = true;
    }

    void Hold(ISessionWorkspace? workspace, TimeProvider time) {
        if (workspace is null || !_cover.IsVisible) {
            Lift();
            return;
        }
        var generation = ++_generation;
        // Installed before subscribing: a workspace already painted lifts during Subscribe, and that lift must find
        // the hold to dispose. Adding to a disposed hold disposes the addition at once.
        var hold = new CompositeDisposable(time.CreateTimer(_ => Dispatcher.UIThread.Post(() => LiftIfHeld(generation)), null, MaxHold, Timeout.InfiniteTimeSpan));
        _hold.Disposable = hold;
        hold.Add(Painted(workspace).Where(p => p).Take(1).Subscribe(_ => AfterLayout(generation)));
    }

    void LiftIfHeld(int generation) {
        if (generation == _generation) Lift();
    }

    // Two frames: the first lays out the new rows, the second the scroll to their tail.
    void AfterLayout(int generation) {
        if (TopLevel.GetTopLevel(_pane) is not { } top) {
            Lift();
            return;
        }
        top.RequestAnimationFrame(_ => top.RequestAnimationFrame(_ => LiftIfHeld(generation)));
    }

    void Lift() {
        _generation++;
        _hold.Disposable = null;
        _cover.IsVisible = false;
        var still = _still.Source;
        _still.Source = null;
        still?.Dispose();
    }

    static IObservable<bool> Painted(ISessionWorkspace workspace) => workspace switch {
        WorkspaceViewModel local => Observable.Switch(local
            .WhenAnyValue(w => w.Chat, w => w.IsStarting, (chat, starting) => starting ? Observable.Return(true) : Read(chat))),
        RemoteSessionViewModel remote => Read(remote.Chat),
        _ => Observable.Return(true),
    };

    static IObservable<bool> Read(ChatTabViewModel? chat) =>
        chat is null ? Observable.Return(false) : chat.WhenAnyValue(c => c.Phase).Select(phase => phase != ChatTabPhase.Waiting);
}
