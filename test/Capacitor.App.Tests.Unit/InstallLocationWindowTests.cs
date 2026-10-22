using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationWindowTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_shared_install_defaults_to_Applications_and_can_retry_for_the_current_user() {
        await AvaloniaSession.DispatchAsync(async () => {
            const string userApps = "/Users/example/Applications";
            var chosen = "/Applications";
            var attempts = new List<string>();
            static ApplicationsInstallPlan Plan(string path) => new(ApplicationsInstallAction.Install, path + "/kcap.app");
            var window = new InstallLocationWindow("Install kcap.",
                () => {
                    attempts.Add(chosen);
                    return Task.FromResult(chosen == "/Applications"
                        ? new MoveOutcome(false, null, "Permission denied", PermissionDenied: true)
                        : new MoveOutcome(true, chosen + "/kcap.app", null));
                }, () => { }, plan: Plan(chosen), destinations: ["/Applications", userApps],
                selectDestination: path => { chosen = path; return Plan(path); },
                defaultDestination: "/Applications", userApplicationsDirectory: userApps);
            window.Show();
            try {
                var button = window.FindControl<Button>("MoveButton")!;
                var picker = window.FindControl<ComboBox>("DestinationPicker")!;
                await Assert.That(picker.SelectedIndex).IsEqualTo(0);
                await Assert.That(button.IsEnabled).IsTrue();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(window.FindControl<SelectableTextBlock>("ErrorText")!.Text).Contains("Install for me only");

                picker.SelectedIndex = 1;
                await Assert.That(chosen).IsEqualTo(userApps);
                await Assert.That(window.FindControl<Border>("ErrorPanel")!.IsVisible).IsFalse();
                await Assert.That(window.FindControl<TextBlock>("Explanation")!.Text).Contains(userApps + "/kcap.app");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(attempts.SequenceEqual(["/Applications", userApps])).IsTrue();
            } finally {
                window.Close();
            }
        });
    }

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
