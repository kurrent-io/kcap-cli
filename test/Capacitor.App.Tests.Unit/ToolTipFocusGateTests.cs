using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class ToolTipFocusGateTests {
    /// A window losing activation closes the tip it shows, and no tip opens while it is inactive.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_inactive_window_shows_no_tooltip() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            ToolTipFocusGate.Install();
            var target = new Button { Content = "x" };
            ToolTip.SetTip(target, "tip");
            var window = new Window { Content = target, Width = 200, Height = 100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try {
                ToolTip.SetIsOpen(target, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetIsOpen(target)).IsTrue();

                AvaloniaSession.LoseKeyboardFocus(window);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(window.IsActive).IsFalse();
                await Assert.That(ToolTip.GetIsOpen(target)).IsFalse();

                ToolTip.SetIsOpen(target, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ToolTip.GetIsOpen(target)).IsFalse();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }
}
