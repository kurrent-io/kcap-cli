using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class PaneLayoutMemoryTests {
    [Test]
    public async Task Missing_or_invalid_width_uses_the_minimum() {
        await Assert.That(PaneLayoutMemory.Resolve(null, 330)).IsEqualTo(330);
        await Assert.That(PaneLayoutMemory.Resolve(double.NaN, 330)).IsEqualTo(330);
    }

    [Test]
    public async Task Saved_width_is_raised_to_the_minimum_and_otherwise_kept() {
        await Assert.That(PaneLayoutMemory.Resolve(212, 330)).IsEqualTo(330);
        await Assert.That(PaneLayoutMemory.Resolve(5000, 330)).IsEqualTo(5000);
    }

    [Test]
    public async Task Every_change_is_persisted_and_restored() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var panes = new PaneLayout();
        PaneLayoutMemory.Attach(panes, store);

        panes.RailWidth = 412;
        panes.ContextWidth = 500;
        panes.ContextCollapsed = true;
        panes.RailCollapsed = true;

        var state = await WaitForAsync(store, s => s.RailCollapsed);
        var restored = new PaneLayout();
        PaneLayoutMemory.Apply(restored, state);
        await Assert.That(restored.RailWidth).IsEqualTo(412);
        await Assert.That(restored.RailCollapsed).IsTrue();
        await Assert.That(restored.ContextWidth).IsEqualTo(500);
        await Assert.That(restored.ContextCollapsed).IsTrue();
    }

    /// A width saved on a larger screen must not push the splitter off a smaller window.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_never_lays_out_wider_than_its_share_of_the_window() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailWidth = 5000;
            window.Show();
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(1400 * PaneLayout.MaxShare);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_finished_rail_drag_updates_the_layout() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Show();
            window.SessionsSurface.ColumnDefinitions[0].Width = new GridLength(412);
            window.UpdateLayout();
            window.RailSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(82, 0) });
            await Assert.That(window.Panes.RailWidth).IsEqualTo(412);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsing_the_rail_hides_it_and_the_expand_button_brings_it_back_at_its_width() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailWidth = 400;
            window.Show();

            window.SessionRail.FindControl<Button>("CollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.Panes.RailCollapsed).IsTrue();
            await Assert.That(window.SessionRail.IsVisible).IsFalse();
            await Assert.That(window.RailSplitter.IsVisible).IsFalse();
            await Assert.That(window.RailExpandButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(window.ContentPane.Bounds.X).IsEqualTo(0);

            window.RailExpandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(400);
            await Assert.That(window.RailExpandButton.IsVisible).IsFalse();
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_context_pane_follows_the_window_layout_and_its_header_toggle() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.ContextWidth = 450;
            var view = new WorkspaceView();
            window.ContentPane.Children.Add(view);
            window.Show();
            window.UpdateLayout();
            var pane = view.FindControl<WorkContextView>("WorkContextHost")!;
            await Assert.That(pane.Bounds.Width).IsEqualTo(450);

            view.FindControl<Button>("ContextToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.Panes.ContextCollapsed).IsTrue();
            await Assert.That(pane.IsVisible).IsFalse();

            window.Panes.ContextCollapsed = false;
            window.Panes.ContextWidth = 100;
            window.UpdateLayout();
            await Assert.That(pane.Bounds.Width).IsEqualTo(PaneLayout.ContextMinWidth);
            window.Close();
        });
    }

    // Saves are fire-and-forget.
    static async Task<AppState> WaitForAsync(AppStateStore store, Func<AppState, bool> done) {
        for (var i = 0; i < 100; i++) {
            var state = await store.LoadAsync();
            if (done(state)) return state;
            await Task.Delay(20);
        }

        return await store.LoadAsync();
    }
}
