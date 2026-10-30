using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The page viewer on a runtime with no usable WebView: it says the page opens in the browser and
/// never constructs a NativeWebView.
public class PageViewerSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task No_webview_shows_the_browser_fallback_without_a_native_webview() => RunOnUiAsync(async () => {
        var vm = new PageViewerViewModel(new FakePageSource(), "01ec", "https://srv.test/pages/01ec", new RecordingOpener(), TimeProvider.System, webViewAvailable: false);
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
}
