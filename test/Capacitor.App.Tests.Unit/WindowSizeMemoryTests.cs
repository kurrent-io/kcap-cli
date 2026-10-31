using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class WindowSizeMemoryTests {
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

    static ScreenArea ScreenOf(int width, int height, int taskbar, double scaling = 1, int x = 0) =>
        new(new PixelRect(x, 0, width, height), new PixelRect(x, 0, width, height - taskbar), scaling);

    static readonly ScreenArea Primary = ScreenOf(1920, 1080, 40);

    [Test]
    public async Task With_no_remembered_position_the_default_is_centred_on_the_first_work_area() {
        await Assert.That(WindowSizeMemory.Place(new AppState(), [Primary]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(260, 140), new Size(WindowSizeMemory.DefaultWidth, WindowSizeMemory.DefaultHeight)));
    }

    [Test]
    public async Task A_remembered_frame_inside_its_work_area_is_kept() {
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowWidth: 1552, WindowHeight: 888, WindowX: 120, WindowY: 80), [Primary]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(120, 80), new Size(1552, 888)));
    }

    /// The default is taller than a 1280x800 screen's work area: the window takes that area rather
    /// than opening with its bottom edge under the taskbar.
    [Test]
    public async Task The_default_shrinks_into_a_small_work_area() {
        await Assert.That(WindowSizeMemory.Place(new AppState(), [ScreenOf(1280, 800, 48)]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(0, 0), new Size(1280, 752)));
    }

    [Test]
    public async Task At_125_percent_on_a_768_line_laptop_both_sides_shrink_to_the_work_area() {
        await Assert.That(WindowSizeMemory.Place(new AppState(), [ScreenOf(1366, 768, 40, 1.25)]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(0, 0), new Size(1366 / 1.25, 728 / 1.25)));
    }

    [Test]
    public async Task A_remembered_frame_running_under_the_taskbar_moves_up_into_the_work_area() {
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowWidth: 1552, WindowHeight: 888, WindowX: 100, WindowY: 300), [Primary]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(100, 152), new Size(1552, 888)));
    }

    /// The whole frame comes back, not just its top-left pixel, so a window from a removed monitor
    /// is fully on the first screen.
    [Test]
    public async Task A_remembered_position_off_every_screen_brings_the_whole_frame_onto_the_first() {
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowX: 8000, WindowY: -400), [Primary]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(520, 0), new Size(WindowSizeMemory.DefaultWidth, WindowSizeMemory.DefaultHeight)));
    }

    /// A window placed across two monitors on purpose stays there: the two work areas hold it whole.
    [Test]
    public async Task A_remembered_frame_straddling_two_monitors_inside_their_work_areas_is_kept() {
        var right = ScreenOf(1920, 1080, 40, x: 1920);
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowX: 1500, WindowY: 80), [Primary, right]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(1500, 80), new Size(WindowSizeMemory.DefaultWidth, WindowSizeMemory.DefaultHeight)));
    }

    [Test]
    public async Task A_straddling_frame_dipping_under_a_taskbar_lands_whole_on_the_monitor_holding_most_of_it() {
        var right = ScreenOf(1920, 1080, 40, x: 1920);
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowX: 1500, WindowY: 300), [Primary, right]))
            .IsEqualTo(((PixelPoint?)new PixelPoint(1920, 280), new Size(WindowSizeMemory.DefaultWidth, WindowSizeMemory.DefaultHeight)));
    }

    [Test]
    public async Task With_no_screen_to_ask_the_remembered_position_is_used_as_saved() {
        await Assert.That(WindowSizeMemory.Place(new AppState(WindowX: 120, WindowY: 80), []).Position).IsEqualTo(new PixelPoint(120, 80));
        await Assert.That(WindowSizeMemory.Place(new AppState(), []).Position).IsNull();
    }

    static readonly Size Floor = new(WindowSizeMemory.MinWidth, WindowSizeMemory.MinHeight);

    [Test]
    public async Task The_minimum_is_the_floor_where_the_screen_holds_it() {
        await Assert.That(WindowSizeMemory.Minimum(Floor, 1.0, new Size(1280, 752))).IsEqualTo(Floor);
        await Assert.That(WindowSizeMemory.Minimum(Floor, 1.0, null)).IsEqualTo(Floor);
    }

    /// Otherwise the platform would hold the window wider than the work area it was fitted into.
    [Test]
    public async Task The_minimum_never_exceeds_the_work_area() {
        await Assert.That(WindowSizeMemory.Minimum(Floor, 1.0, new Size(1024, 688))).IsEqualTo(new Size(1024, WindowSizeMemory.MinHeight));
    }

    [Test]
    public async Task A_content_scale_raises_the_minimum_within_the_work_area() {
        await Assert.That(WindowSizeMemory.Minimum(Floor, 1.5, new Size(1920, 1040))).IsEqualTo(new Size(1800, 840));
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

    /// Quitting from the dock while minimized must not forget that the window was maximized.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_window_minimized_from_maximized_is_remembered_as_maximized() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = WindowAt(1552, 888);
            WindowSizeMemory.Attach(window, store);
            window.Show();
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            window.Close();
        });

        var state = await store.LoadAsync();
        await Assert.That(state.WindowMaximized).IsTrue();
    }

    static Window WindowAt(double width, double height) => new() {
        MinWidth = WindowSizeMemory.MinWidth,
        MinHeight = WindowSizeMemory.MinHeight,
        Width = width,
        Height = height,
    };
}
