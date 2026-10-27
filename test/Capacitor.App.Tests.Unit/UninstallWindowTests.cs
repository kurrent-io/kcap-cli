using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class UninstallWindowTests {
    [Test]
    public Task Handoff_replaces_normal_windows_immediately_and_cannot_be_dismissed() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var main = new Window();
            var settings = new Window();
            main.Show();
            settings.Show();
            var progress = UninstallWindow.ShowHandoff([main, settings]);
            try {
                Dispatcher.UIThread.RunJobs();
                await Assert.That(main.IsVisible).IsFalse();
                await Assert.That(settings.IsVisible).IsFalse();
                await Assert.That(progress.IsVisible).IsTrue();
                await Assert.That(progress.IsActive).IsTrue();
                await Assert.That(progress.FindControl<Control>("HandoffContent")!.IsVisible).IsTrue();
                await Assert.That(progress.FindControl<Control>("RunContent")!.IsVisible).IsFalse();

                progress.Close();
                await Assert.That(progress.IsVisible).IsTrue();
            } finally {
                progress.Closing += (_, e) => e.Cancel = false;
                progress.Close();
                main.Close();
                settings.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
}
