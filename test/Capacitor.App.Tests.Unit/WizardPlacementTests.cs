using Avalonia;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class WizardPlacementTests {
    static ScreenArea Screen(int width, int height, int taskbar, double scaling, int x = 0) =>
        new(new PixelRect(x, 0, width, height), new PixelRect(x, 0, width, height - taskbar), scaling);

    [Test]
    public async Task A_screen_with_room_gets_the_preferred_size_centred_on_its_work_area() {
        var placed = WizardPlacement.Place(1.0, Screen(1920, 1080, 40, 1.0));

        await Assert.That(placed).IsEqualTo((new PixelPoint(580, 140), new Size(760, 760)));
    }

    /// 760 units are 1140 pixels at 150%, taller than a 1080p work area: the window takes the work
    /// area's height instead of hanging its footer under the taskbar.
    [Test]
    public async Task At_150_percent_on_1080p_the_height_shrinks_to_the_work_area() {
        var placed = WizardPlacement.Place(1.0, Screen(1920, 1080, 48, 1.5));

        await Assert.That(placed).IsEqualTo((new PixelPoint(390, 0), new Size(760, 1032 / 1.5)));
    }

    [Test]
    public async Task At_125_percent_on_a_768_line_laptop_the_height_shrinks_to_the_work_area() {
        var placed = WizardPlacement.Place(1.0, Screen(1366, 768, 40, 1.25));

        await Assert.That(placed).IsEqualTo((new PixelPoint(208, 0), new Size(760, 728 / 1.25)));
    }

    [Test]
    public async Task A_screen_smaller_than_the_window_both_ways_is_filled_exactly() {
        var placed = WizardPlacement.Place(1.0, Screen(1024, 768, 40, 1.5));

        await Assert.That(placed).IsEqualTo((new PixelPoint(0, 0), new Size(1024 / 1.5, 728 / 1.5)));
    }

    /// The work area's own origin counts: a second monitor to the right, with its taskbar docked left.
    [Test]
    public async Task The_window_is_centred_on_the_work_area_of_its_own_screen() {
        var screen = new ScreenArea(new PixelRect(1920, 0, 2560, 1440), new PixelRect(1968, 0, 2512, 1440), 1.0);

        var placed = WizardPlacement.Place(1.0, screen);

        await Assert.That(placed).IsEqualTo((new PixelPoint(2844, 340), new Size(760, 760)));
    }

    /// A magnified window opens at the size its minimum asks for, rather than growing past the
    /// screen's edge once the minimum is applied after it has been placed.
    [Test]
    public async Task A_content_scale_opens_the_window_at_its_scaled_minimum() {
        var placed = WizardPlacement.Place(1.5, Screen(1920, 1080, 40, 1.0));

        await Assert.That(placed).IsEqualTo((new PixelPoint(420, 70), new Size(1080, 900)));
    }

    [Test]
    public async Task The_minimum_is_the_onboarding_minimum_where_the_screen_holds_it() {
        await Assert.That(WizardPlacement.Minimum(1.0, new Size(1280, 688)))
            .IsEqualTo(new Size(WindowSizeMemory.OnboardingMinWidth, WindowSizeMemory.OnboardingMinHeight));
        await Assert.That(WizardPlacement.Minimum(1.0, null))
            .IsEqualTo(new Size(WindowSizeMemory.OnboardingMinWidth, WindowSizeMemory.OnboardingMinHeight));
    }

    /// Otherwise the window could not shrink to fit, and the operating system would hold it larger than the screen.
    [Test]
    public async Task The_minimum_never_exceeds_the_work_area() {
        await Assert.That(WizardPlacement.Minimum(1.0, new Size(1093, 582.4))).IsEqualTo(new Size(720, 582.4));
        await Assert.That(WizardPlacement.Minimum(1.5, new Size(682, 485))).IsEqualTo(new Size(682, 485));
    }
}
