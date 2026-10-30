using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Update;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class UpdateWindowTests {
    [Test]
    public Task Progress_refresh_keeps_running_indicators_attached() => AvaloniaSession.RunOnUiAsync(async () => {
        using var local = new BehaviorSubject<LocalStepInput?>(new(true, null, null, null,
            Phase: LocalUpgradePhase.Downloading, From: "1.4.0", To: "1.4.1", DownloadFraction: 0.1));
        using var vm = new UpdateFinishViewModel(
            new("op", AppUpdateTransition.InApp, "1.4.0", "1.4.1", DateTimeOffset.UnixEpoch),
            Observable.Return(new AttachStatus(AttachState.Connected, null, [])),
            Observable.Return<(string?, string?)>(("1.4.1", "connected")), Observable.Return(false),
            local, new FakeTimeProvider());
        var window = new UpdateFinishWindow { DataContext = vm };
        try {
            window.Show();
            Render(window);
            var indicators = window.GetVisualDescendants().OfType<AppIcon>()
                .Where(icon => icon.Classes.Contains("kcapSpinning")).ToArray();
            await Assert.That(indicators.Length).IsEqualTo(2);
            var detachments = 0;
            foreach (var icon in indicators) icon.DetachedFromVisualTree += (_, _) => detachments++;

            local.OnNext(local.Value! with { DownloadFraction = 0.42 });
            Render(window);

            await Assert.That(detachments).IsEqualTo(0);
            await Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "42%")).IsTrue();
            local.OnNext(local.Value! with { Phase = LocalUpgradePhase.Verifying, DownloadFraction = null });
            Render(window);
            await Assert.That(detachments).IsEqualTo(0);
            await Assert.That(window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Text == "Verifying the download")).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task Long_failure_details_scroll_without_hiding_the_open_action() => AvaloniaSession.RunOnUiAsync(async () => {
        var error = "Download failed: https://example.test/" + new string('x', 2000);
        var failure = new LocalUpgradeOutcome.Failed("download_failed", error, false,
            LocalInstallAffordance.For("download_failed"), "1.4.0", "1.4.1");
        using var vm = new UpdateFinishViewModel(
            new("op", AppUpdateTransition.InApp, "1.4.0", "1.4.1", DateTimeOffset.UnixEpoch),
            Observable.Return(new AttachStatus(AttachState.Connected, null, [])),
            Observable.Return<(string?, string?)>(("1.4.1", "connected")), Observable.Return(false),
            Observable.Return<LocalStepInput?>(new(false, failure, null, null)), new FakeTimeProvider());
        var window = new UpdateFinishWindow { DataContext = vm, Height = 320 };
        try {
            window.Show();
            Render(window);
            var details = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && Equals(b.Content, "Details"));
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Render(window);

            var diagnostic = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == error);
            await Assert.That(diagnostic.IsVisible).IsTrue();
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            await Assert.That(scroll.Extent.Height).IsGreaterThan(scroll.Viewport.Height);
            var open = window.FindControl<Button>("OpenButton")!;
            var bottom = open.TranslatePoint(new Point(open.Bounds.Width, open.Bounds.Height), window)!.Value;
            await Assert.That(open.IsVisible && open.IsEnabled).IsTrue();
            await Assert.That(bottom.Y).IsLessThanOrEqualTo(window.ClientSize.Height);

            var opened = false;
            vm.OpenRequested += () => opened = true;
            open.Command!.Execute(open.CommandParameter);
            await Assert.That(opened).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task Manual_check_progress_changes_from_indeterminate_to_download_percentage() => AvaloniaSession.RunOnUiAsync(async () => {
        var window = new UpdateCheckWindow { DataContext = new UpdateCheckProgress("Checking for updates…") };
        try {
            window.Show();
            Render(window);
            var progress = window.GetVisualDescendants().OfType<ProgressBar>().Single();
            await Assert.That(progress.IsIndeterminate).IsTrue();

            window.DataContext = new UpdateCheckProgress("Downloading kcap 1.4.1…", 42);
            Render(window);
            await Assert.That(progress.IsIndeterminate).IsFalse();
            await Assert.That(progress.Value).IsEqualTo(42);
            await Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "42%")).IsTrue();

            var dismiss = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Dismiss"));
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.That(window.IsVisible).IsFalse();
        } finally { window.Close(); }
    });

    static void Render(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
