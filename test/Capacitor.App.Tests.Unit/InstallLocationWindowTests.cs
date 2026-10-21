using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationWindowTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Destination_selection_controls_the_install_action_and_a_failure_allows_retry() {
        await AvaloniaSession.DispatchAsync(async () => {
            var calls = 0;
            var chosen = "";
            var window = new InstallLocationWindow("Choose an installation.",
                () => { calls++; return Task.FromResult(new MoveOutcome(false, null, "Could not quit the installed copy.")); },
                () => { }, destinations: ["/Applications", "/Users/example/Applications"],
                selectDestination: path => {
                    chosen = path;
                    return new(path == "/Applications" ? ApplicationsInstallAction.Blocked : ApplicationsInstallAction.Update,
                        path + "/kcap.app", "1.1.0", "1.0.0", Error: "Another app occupies this destination.");
                });
            window.Show();
            var button = window.FindControl<Button>("MoveButton")!;
            var picker = window.FindControl<ComboBox>("DestinationPicker")!;
            await Assert.That(button.IsEnabled).IsFalse();
            picker.SelectedIndex = 0;
            await Assert.That(button.IsEnabled).IsFalse();
            picker.SelectedIndex = 1;
            await Assert.That(chosen).IsEqualTo("/Users/example/Applications");
            await Assert.That(button.IsEnabled).IsTrue();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(window.FindControl<Border>("ErrorPanel")!.IsVisible).IsTrue();
            await Assert.That(button.IsEnabled).IsTrue();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.That(calls).IsEqualTo(2);
            window.Close();
        });
    }
}
