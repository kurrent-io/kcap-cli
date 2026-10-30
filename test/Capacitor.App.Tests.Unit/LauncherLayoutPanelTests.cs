using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LauncherLayoutPanelTests {
    /// The composer stays centred in the whole pane while the notices fit above it, and moves down
    /// only as far as the notices and the gap need, so it is never covered.
    [Test]
    [Arguments(0, 300)]
    [Arguments(100, 300)]
    [Arguments(400, 420)]
    public async Task The_composer_moves_down_only_when_the_notices_would_reach_it(double noticeHeight, double composerTop) {
        var top = await AvaloniaSession.DispatchAsync(() => {
            var notices = new Border { Height = noticeHeight, IsVisible = noticeHeight > 0 };
            var composer = new Border { Height = 200 };
            var panel = new LauncherLayoutPanel { Gap = 20, Children = { notices, composer } };
            panel.Measure(new Size(600, 800));
            panel.Arrange(new Rect(0, 0, 600, 800));
            return composer.Bounds.Y;
        });

        await Assert.That(top).IsEqualTo(composerTop);
    }
}
