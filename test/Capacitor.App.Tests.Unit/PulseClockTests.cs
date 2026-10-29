using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

/// Every in-flight marker fades on PulseClock: one DispatcherTimer at ten ticks a second. A
/// keyframe animation in a style runs per marker at the display's refresh rate and keeps the
/// compositor drawing the whole window every frame, which is what a busy app's CPU went on.
public class PulseClockTests {
    [Test]
    public async Task The_clock_ticks_ten_times_a_second() =>
        await Assert.That(PulseClock.TickInterval).IsEqualTo(TimeSpan.FromMilliseconds(100));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_style_pulses_a_marker_with_keyframes() {
        var offenders = await AvaloniaSession.DispatchAsync(() =>
            Animated(Application.Current!.Styles)
                .Select(style => style.Selector?.ToString() ?? "<none>")
                .Where(selector => selector.Contains("pulsing") || selector.Contains("toolRunning"))
                .ToList());
        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_marker_fades_on_the_clock_while_active_and_is_steady_once_it_stops() {
        var (whileActive, clock, afterStop) = await AvaloniaSession.DispatchAsync(() => {
            var marker = new Border();
            var window = new Window { Content = marker, Width = 100, Height = 100 };
            window.Show();
            PulseClock.SetIsActive(marker, true);
            var active = marker.Opacity;
            var now = PulseClock.Opacity;
            PulseClock.SetIsActive(marker, false);
            var stopped = marker.Opacity;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (active, now, stopped);
        });
        await Assert.That(whileActive).IsEqualTo(clock).Within(0.05);
        await Assert.That(afterStop).IsEqualTo(1.0);
    }

    /// A collapsed section keeps its rows attached with IsVisible false, so an active marker there
    /// must not be painted on every tick: that is a render pass for something nobody can see.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_marker_in_a_collapsed_section_is_left_alone_until_the_section_shows() {
        var (whileHidden, whenShown, clock) = await AvaloniaSession.DispatchAsync(() => {
            var marker = new Border();
            var section = new Panel { IsVisible = false, Children = { marker } };
            var window = new Window { Content = section, Width = 100, Height = 100 };
            window.Show();
            PulseClock.SetIsActive(marker, true);
            PulseClock.Tick();
            var hidden = marker.Opacity;
            section.IsVisible = true;
            PulseClock.Tick();
            var shown = marker.Opacity;
            var now = PulseClock.Opacity;
            PulseClock.SetIsActive(marker, false);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (hidden, shown, now);
        });
        await Assert.That(whileHidden).IsEqualTo(1.0);
        await Assert.That(whenShown).IsEqualTo(clock).Within(0.05);
    }

    static IEnumerable<Style> Animated(IEnumerable<IStyle> styles) {
        foreach (var style in styles) {
            switch (style) {
                case Style s when s.Animations.Count > 0:
                    yield return s;
                    break;
                case Styles nested:
                    foreach (var inner in Animated(nested)) yield return inner;
                    break;
                case StyleInclude { Loaded: { } loaded }:
                    foreach (var inner in Animated([loaded])) yield return inner;
                    break;
            }
        }
    }
}
