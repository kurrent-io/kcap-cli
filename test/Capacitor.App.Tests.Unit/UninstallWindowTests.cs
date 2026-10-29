using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class UninstallWindowTests {
    sealed class SuccessfulRemoval : IAppRemoval {
        public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public bool DeleteSupportFiles() => true;
        public Task<bool> TrashBundleAsync(CancellationToken ct) => Task.FromResult(true);
        public void RevealBundle() => throw new InvalidOperationException("Successful removal should not need Finder.");
    }

    static async Task SavePreviewAsync(Window window, string suffix) {
        if (Environment.GetEnvironmentVariable("KCAP_UNINSTALL_PREVIEW") is not { Length: > 0 } preview) return;
        await Task.Delay(250);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();
        var path = Path.Combine(Path.GetDirectoryName(preview)!, Path.GetFileNameWithoutExtension(preview) + suffix + ".png");
        using var bitmap = window.CaptureRenderedFrame();
        bitmap!.Save(path, PngBitmapEncoderOptions.Default);
    }

    [Test]
    public Task Preparation_progress_and_success_share_one_window() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var quits = 0;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cli = new FakeKcapCli { UninstallBehavior = onLine => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "Removed daemon service 'kcap'."));
                return Task.FromResult(new StreamingResult(0, false, []));
            } };
            var run = new UninstallRunViewModel(cli, new SuccessfulRemoval(), false, () => quits++, action => action(), () => ready.Task);
            var main = new Window();
            var settings = new Window();
            main.Show();
            settings.Show();
            var progress = UninstallWindow.ShowProgress([main, settings], run);
            try {
                var operation = run.RunAsync();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(main.IsVisible).IsFalse();
                await Assert.That(settings.IsVisible).IsFalse();
                await Assert.That(progress.IsVisible).IsTrue();
                await Assert.That(progress.IsActive).IsTrue();
                await Assert.That(progress.Topmost).IsTrue();
                await Assert.That(run.State).IsEqualTo(UninstallRunState.Preparing);
                await Assert.That(progress.FindControl<ProgressBar>("Progress")!.IsVisible).IsTrue();
                await Assert.That(progress.FindControl<ProgressBar>("Progress")!.IsIndeterminate).IsTrue();
                await Assert.That(progress.FindControl<Control>("HistoryPanel")!.IsVisible).IsFalse();
                await SavePreviewAsync(progress, "-preparing");
                progress.Close();
                await Assert.That(progress.IsVisible).IsTrue();

                ready.SetResult();
                await operation;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(quits).IsEqualTo(0);
                await Assert.That(progress.IsVisible).IsTrue();
                await Assert.That(progress.Title).IsEqualTo("kcap is uninstalled");
                await Assert.That(progress.Topmost).IsFalse();
                await Assert.That(progress.FindControl<TextBlock>("RestartAdvice")!.IsVisible).IsTrue();
                await Assert.That(progress.FindControl<ProgressBar>("Progress")!.IsVisible).IsFalse();
                await Assert.That(progress.Width).IsEqualTo(480);
                await Assert.That(progress.ClientSize.Height).IsLessThan(420);
                await SavePreviewAsync(progress, "");

                var details = progress.FindControl<Button>("DetailsButton")!;
                details.Command!.Execute(details.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(progress.FindControl<Control>("HistoryPanel")!.IsVisible).IsTrue();
                await Assert.That(progress.FindControl<Control>("ReceiptList")!.IsVisible).IsTrue();
                await Assert.That(progress.FindControl<Control>("LinesScroll")!.IsVisible).IsFalse();
                await Assert.That(run.DetailsLabel).IsEqualTo("Hide what was removed");
                await SavePreviewAsync(progress, "-details");

                var done = progress.FindControl<Button>("CloseButton")!;
                await Assert.That(done.Content).IsEqualTo("Done");
                await Assert.That(done.IsEnabled).IsTrue();
                await Assert.That(done.IsDefault).IsTrue();
                done.Command!.Execute(done.CommandParameter);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(quits).IsEqualTo(1);
            } finally {
                ready.TrySetResult();
                progress.Closing += (_, e) => e.Cancel = false;
                progress.Close();
                main.Close();
                settings.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    [Test]
    public Task Failure_opens_details_and_keeps_close_available() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var cli = new FakeKcapCli { UninstallBehavior = _ => Task.FromResult(
                new StreamingResult(1, false, [new StreamedLine(ProcessStreamKind.Stderr, "Could not stop the daemon.")])) };
            var run = new UninstallRunViewModel(cli, new SuccessfulRemoval(), false, () => { }, action => action());
            var window = new UninstallWindow { DataContext = run };
            window.Show();
            try {
                await run.RunAsync();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(run.IsFinished).IsFalse();
                await Assert.That(window.Topmost).IsFalse();
                await Assert.That(window.Title).IsEqualTo("kcap was not fully uninstalled");
                await Assert.That(window.FindControl<TextBlock>("RestartAdvice")!.IsVisible).IsFalse();
                await Assert.That(window.FindControl<Control>("HistoryPanel")!.IsVisible).IsTrue();
                await Assert.That(window.FindControl<Control>("LinesScroll")!.IsVisible).IsTrue();
                await Assert.That(window.FindControl<Control>("ReceiptList")!.IsVisible).IsFalse();
                var close = window.FindControl<Button>("CloseButton")!;
                await Assert.That(close.Content).IsEqualTo("Close");
                await Assert.That(close.IsEnabled).IsTrue();
                await SavePreviewAsync(window, "-failed");
            } finally {
                window.Close();
            }
        });
}
