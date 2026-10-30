using Avalonia;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class WindowRestoreTests {
    static readonly ScreenArea Primary = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1);
    static readonly ScreenArea Right = new(new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1);

    [Test]
    public async Task A_frame_inside_its_work_area_is_left_alone() {
        await Assert.That(WindowRestore.FitOnScreen(new PixelPoint(100, 80), new Size(1400, 760), [Primary])).IsNull();
    }

    /// The monitor the window was on has gone: the whole frame comes back onto the remaining one.
    [Test]
    public async Task A_frame_on_a_removed_monitor_comes_back_onto_the_first_screen() {
        var fit = WindowRestore.FitOnScreen(new PixelPoint(2400, 300), new Size(1400, 760), [Primary]);

        await Assert.That(fit).IsEqualTo((new PixelPoint(520, 280), new Size(1400, 760)));
    }

    /// A frame the maximized window left behind is larger than the work area: it shrinks to fit
    /// instead of hanging off the edges.
    [Test]
    public async Task An_oversized_frame_shrinks_to_the_work_area() {
        var fit = WindowRestore.FitOnScreen(new PixelPoint(-8, -8), new Size(1936, 1056), [Primary]);

        await Assert.That(fit).IsEqualTo((new PixelPoint(0, 0), new Size(1920, 1040)));
    }

    /// The same size in device-independent units is a bigger frame once the screen's scaling
    /// rises, and the size handed back is in those units again.
    [Test]
    public async Task A_scaling_change_is_measured_at_the_new_scaling() {
        var scaled = Primary with { Scaling = 1.5 };

        var fit = WindowRestore.FitOnScreen(new PixelPoint(0, 0), new Size(1400, 600), [scaled]);

        await Assert.That(fit).IsEqualTo((new PixelPoint(0, 0), new Size(1280, 600)));
    }

    /// A frame mostly on the second monitor stays there, pulled inside its work area.
    [Test]
    public async Task A_frame_is_fitted_to_the_screen_holding_most_of_it() {
        var fit = WindowRestore.FitOnScreen(new PixelPoint(3500, 900), new Size(1400, 760), [Primary, Right]);

        await Assert.That(fit).IsEqualTo((new PixelPoint(3080, 640), new Size(1400, 760)));
    }

    [Test]
    public async Task No_screens_means_nothing_to_fit_against() {
        await Assert.That(WindowRestore.FitOnScreen(new PixelPoint(9000, 9000), new Size(1400, 760), [])).IsNull();
    }
}
