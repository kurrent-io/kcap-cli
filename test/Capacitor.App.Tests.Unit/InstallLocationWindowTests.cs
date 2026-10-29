using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationWindowTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_denied_shared_install_offers_a_per_user_retry() {
        await AvaloniaSession.RunOnUiAsync(async () => {
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
                }, () => { }, plan: Plan(chosen),
                selectDestination: path => { chosen = path; return Plan(path); },
                userApplicationsDirectory: userApps);
            window.Show();
            try {
                var button = window.FindControl<Button>("MoveButton")!;
                await Assert.That(window.FindControl<StackPanel>("DestinationPanel")!.IsVisible).IsFalse();
                await Assert.That(button.Content).IsEqualTo("Install and open");

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                await Assert.That(chosen).IsEqualTo(userApps);
                await Assert.That(window.FindControl<SelectableTextBlock>("ErrorText")!.Text).Contains($"cannot write to {Path.GetDirectoryName("/Applications/kcap.app")}");
                await Assert.That(window.FindControl<TextBlock>("Explanation")!.Text).Contains(userApps + "/kcap.app");
                await Assert.That(button.Content).IsEqualTo("Install for me only");
                await Assert.That(button.IsEnabled).IsTrue();

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
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var tmp = new TempDir();
            var system = tmp.PathTo("Applications");
            var user = tmp.PathTo("Users/example/Applications");
            var calls = 0;
            var chosen = "";
            var window = new InstallLocationWindow("Choose an installation.",
                () => { calls++; return Task.FromResult(new MoveOutcome(false, null, "Could not quit the installed copy.")); },
                () => { }, destinations: [system, user],
                selectDestination: path => {
                    chosen = path;
                    return new(path == system ? ApplicationsInstallAction.Blocked : ApplicationsInstallAction.Update,
                        path + "/kcap.app", "1.1.0", "1.0.0", Error: "Another app occupies this destination.");
                });
            window.Show();
            var button = window.FindControl<Button>("MoveButton")!;
            var picker = window.FindControl<ComboBox>("DestinationPicker")!;
            await Assert.That(button.IsEnabled).IsFalse();
            picker.SelectedIndex = 0;
            await Assert.That(button.IsEnabled).IsFalse();
            picker.SelectedIndex = 1;
            await Assert.That(chosen).IsEqualTo(user);
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

    /// Something in the way is shown in Finder; coming back re-checks the folder, but never before a destination is
    /// chosen, which would replace the picker with one folder's plan.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_blocked_folder_is_rechecked_on_return_but_not_before_a_destination_is_chosen() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var tmp = new TempDir();
            var system = tmp.PathTo("Applications");
            var user = tmp.PathTo("Users/example/Applications");
            var occupied = tmp.CreateDir("Applications/kcap.app");
            var inspections = 0;
            ApplicationsInstallPlan Plan(string path) {
                inspections++;
                return Directory.Exists(path + "/kcap.app")
                    ? new(ApplicationsInstallAction.Blocked, path + "/kcap.app", "1.1.0", Error: "Something is in the way.")
                    : new(ApplicationsInstallAction.Install, path + "/kcap.app", "1.1.0");
            }
            var window = new InstallLocationWindow("Choose an installation.", () => Task.FromResult(new MoveOutcome(false, null, null)),
                () => { }, plan: Plan(system), destinations: [system, user], selectDestination: Plan);
            var other = new Window();
            void ComeBack() {
                other.Show();
                other.Activate();
                window.Activate();
                Dispatcher.UIThread.RunJobs();
            }
            window.Show();
            try {
                var button = window.FindControl<Button>("MoveButton")!;
                var picker = window.FindControl<ComboBox>("DestinationPicker")!;
                var before = inspections;

                ComeBack();
                await Assert.That(inspections).IsEqualTo(before);
                await Assert.That(window.FindControl<TextBlock>("Heading")!.Text).IsEqualTo("Choose an install location");

                picker.SelectedIndex = 0;
                await Assert.That(button.Content).IsEqualTo("Show in Finder");
                await Assert.That(button.IsEnabled).IsTrue();

                Directory.Delete(occupied);
                ComeBack();
                await Assert.That(button.Content).IsEqualTo("Install and open");
            } finally {
                other.Close();
                window.Close();
            }
        });
    }
}
