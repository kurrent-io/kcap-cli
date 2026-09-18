using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class WindowSizeMemoryTests {
    static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Test]
    public async Task Missing_size_uses_the_defaults() {
        var (width, height) = WindowSizeMemory.Resolve(null, null);
        await Assert.That(width).IsEqualTo(WindowSizeMemory.DefaultWidth);
        await Assert.That(height).IsEqualTo(WindowSizeMemory.DefaultHeight);
    }

    [Test]
    public async Task Saved_size_is_kept_when_at_or_above_the_floor() {
        var (width, height) = WindowSizeMemory.Resolve(1552, 888);
        await Assert.That(width).IsEqualTo(1552);
        await Assert.That(height).IsEqualTo(888);
    }

    [Test]
    public async Task Saved_size_below_the_floor_is_raised() {
        var (width, height) = WindowSizeMemory.Resolve(800, 400);
        await Assert.That(width).IsEqualTo(WindowSizeMemory.MinWidth);
        await Assert.That(height).IsEqualTo(WindowSizeMemory.MinHeight);
    }

    [Test]
    public async Task Non_finite_size_uses_the_defaults() {
        var (width, height) = WindowSizeMemory.Resolve(double.NaN, double.PositiveInfinity);
        await Assert.That(width).IsEqualTo(WindowSizeMemory.DefaultWidth);
        await Assert.That(height).IsEqualTo(WindowSizeMemory.DefaultHeight);
    }

    [Test]
    public async Task Missing_position_is_left_unspecified() {
        await Assert.That(WindowSizeMemory.ResolvePosition(null, null, [Screen])).IsNull();
    }

    [Test]
    public async Task Saved_position_on_a_screen_is_kept() {
        await Assert.That(WindowSizeMemory.ResolvePosition(120, 80, [Screen])).IsEqualTo(new PixelPoint(120, 80));
    }

    [Test]
    public async Task Saved_position_off_every_screen_is_clamped_onto_the_first() {
        await Assert.That(WindowSizeMemory.ResolvePosition(8000, -400, [Screen])).IsEqualTo(new PixelPoint(1919, 0));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Apply_restores_the_remembered_placement() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = WindowAt(WindowSizeMemory.DefaultWidth, WindowSizeMemory.DefaultHeight);
            WindowSizeMemory.Apply(window, new AppState(WindowWidth: 1552, WindowHeight: 888, WindowX: 120, WindowY: 80));
            await Assert.That(window.Width).IsEqualTo(1552);
            await Assert.That(window.Height).IsEqualTo(888);
            await Assert.That(window.Position).IsEqualTo(new PixelPoint(120, 80));
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Attach_persists_placement_on_close() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = WindowAt(1552, 888);
            WindowSizeMemory.Attach(window, store);
            window.Show();
            window.Position = new PixelPoint(120, 80);
            window.Close();
        });

        var state = await store.LoadAsync();
        await Assert.That(state.WindowWidth).IsEqualTo(1552);
        await Assert.That(state.WindowHeight).IsEqualTo(888);
        await Assert.That(state.WindowX).IsEqualTo(120);
        await Assert.That(state.WindowY).IsEqualTo(80);
        await Assert.That(state.WindowMaximized).IsFalse();
    }

    static Window WindowAt(double width, double height) => new() {
        MinWidth = WindowSizeMemory.MinWidth,
        MinHeight = WindowSizeMemory.MinHeight,
        Width = width,
        Height = height,
    };
}
