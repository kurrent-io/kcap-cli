using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Pages;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The page viewer's notices: none of these states constructs a NativeWebView.
public class PageViewerSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task No_webview_shows_the_browser_fallback_without_a_native_webview() => RunOnUiAsync(async () => {
        var vm = new PageViewerViewModel(new FakePageContentSource(), "01ec", "https://srv.test/pages/01ec", new RecordingOpener(), TimeProvider.System, webViewAvailable: false);
        var window = new Window { Content = new PageViewer { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        await vm.LoadAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");
        await Assert.That(texts).Contains("Pages open in your browser on this system.");
        await Assert.That(window.GetVisualDescendants().OfType<NativeWebView>().Any()).IsFalse();
        window.Close();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failed_load_offers_a_retry_that_reads_the_page_again() => RunOnUiAsync(async () => {
        var source = new FakePageContentSource();
        source.EnqueueContent(new PageContentRead.Unreachable(), new PageContentRead.NotFound());
        var vm = new PageViewerViewModel(source, "01ec", "https://srv.test/pages/01ec", new RecordingOpener(), TimeProvider.System, webViewAvailable: true);
        var window = new Window { Content = new PageViewer { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        await vm.LoadAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var retry = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Retry");
        await Assert.That(retry.IsEffectivelyVisible).IsTrue();

        retry.Command!.Execute(null);
        for (var i = 0; i < 200 && vm.State is PageViewerState.Unreachable or PageViewerState.Loading; i++) {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        window.UpdateLayout();

        await Assert.That(source.ContentRequested.Count).IsEqualTo(2);
        await Assert.That(vm.State).IsEqualTo(PageViewerState.NotFound);
        await Assert.That(retry.IsEffectivelyVisible).IsFalse();
        window.Close();
    });
}
