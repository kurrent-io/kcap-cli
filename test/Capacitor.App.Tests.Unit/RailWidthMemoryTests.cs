using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class RailWidthMemoryTests {
    [Test]
    public async Task Missing_or_invalid_width_uses_the_default() {
        await Assert.That(RailWidthMemory.Resolve(null)).IsEqualTo(RailWidthMemory.DefaultWidth);
        await Assert.That(RailWidthMemory.Resolve(double.NaN)).IsEqualTo(RailWidthMemory.DefaultWidth);
        await Assert.That(RailWidthMemory.Resolve(-10)).IsEqualTo(RailWidthMemory.DefaultWidth);
    }

    [Test]
    public async Task Saved_width_is_kept_however_narrow_or_wide() {
        await Assert.That(RailWidthMemory.Resolve(0)).IsEqualTo(0);
        await Assert.That(RailWidthMemory.Resolve(212)).IsEqualTo(212);
        await Assert.That(RailWidthMemory.Resolve(5000)).IsEqualTo(5000);
    }

    /// A width saved on a larger screen must not push the splitter off a smaller window.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_never_lays_out_wider_than_its_share_of_the_window() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.RailWidth = 5000;
            window.Show();
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(1400 * RailWidthMemory.MaxShare);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_finished_drag_persists_the_width_and_the_next_window_restores_it() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await AvaloniaSession.RunOnUiAsync(() => {
            var window = new MainWindow();
            RailWidthMemory.Attach(window, store);
            window.RailWidth = 412;
            window.RailSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(82, 0) });
            window.Close();
            return Task.CompletedTask;
        });

        var state = await WaitForRailWidthAsync(store);
        await Assert.That(state.RailWidth).IsEqualTo(412);

        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow();
            RailWidthMemory.Apply(window, state);
            await Assert.That(window.RailWidth).IsEqualTo(412);
            window.Close();
        });
    }

    // The drag's save is fire-and-forget.
    static async Task<AppState> WaitForRailWidthAsync(AppStateStore store) {
        for (var i = 0; i < 100; i++) {
            var state = await store.LoadAsync();
            if (state.RailWidth is not null) return state;
            await Task.Delay(20);
        }

        return await store.LoadAsync();
    }
}
