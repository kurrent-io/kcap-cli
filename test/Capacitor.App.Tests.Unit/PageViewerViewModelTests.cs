using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// What the in-app page viewer shows for each content read, the browser fallback when no WebView
/// can host the page, and which WebView navigations it lets through.
public class PageViewerViewModelTests {
    const string Id = "01ec";
    const string BrowserUrl = "https://srv.test/pages/01ec";

    static ArtefactDto Page(string title = "Release checklist") =>
        new(Id, title, "u1", "org", 3, DateTimeOffset.UtcNow, true, BrowserUrl);

    sealed class Harness {
        public FakePageSource Source { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public PageViewerViewModel Vm { get; }

        public Harness(bool webViewAvailable = true) =>
            Vm = new PageViewerViewModel(Source, Id, BrowserUrl, Opener, new FakeTimeProvider(), webViewAvailable);
    }

    [Test]
    public async Task Found_page_is_ready_with_its_title_and_a_sandboxed_srcdoc_frame() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p class=\"x\">Tom & Jerry</p>"));

        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Ready);
        await Assert.That(h.Vm.Title).IsEqualTo("Release checklist");
        await Assert.That(h.Vm.Html).Contains("sandbox=\"allow-scripts allow-popups\"");
        await Assert.That(h.Vm.Html).Contains("srcdoc=\"&lt;p class=&quot;x&quot;&gt;Tom &amp; Jerry&lt;/p&gt;\"");
        await Assert.That(h.Source.ContentRequested).IsEquivalentTo(new[] { (Id, (int?)null) });
    }

    [Test]
    public async Task Missing_page_is_not_found_with_no_document() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.NotFound());

        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.NotFound);
        await Assert.That(h.Vm.Html).IsNull();
    }

    [Test]
    public async Task Signed_out_and_unreachable_reads_keep_their_own_states() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.SignedOut(), new PageContentRead.Unreachable());

        await h.Vm.LoadAsync(CancellationToken.None);
        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.SignedOut);

        await h.Vm.LoadAsync(CancellationToken.None);
        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Unreachable);
    }

    [Test]
    public async Task Without_a_webview_the_viewer_reads_nothing() {
        var h = new Harness(webViewAvailable: false);

        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.NoWebView);
        await Assert.That(h.Source.ContentRequested).IsEmpty();
        await Assert.That(h.Vm.Html).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_in_browser_hands_the_page_url_to_the_opener() {
        await RunOnUiAsync(async () => {
            var h = new Harness(webViewAvailable: false);

            await h.Vm.OpenInBrowserCommand.Execute();

            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { BrowserUrl });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reload_reads_the_page_again() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.EnqueueContent(new PageContentRead.Unreachable(), new PageContentRead.Found(Page("v2"), 4, "<p>v2</p>"));
            await h.Vm.LoadAsync(CancellationToken.None);

            await h.Vm.ReloadCommand.Execute();

            await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Ready);
            await Assert.That(h.Vm.Title).IsEqualTo("v2");
            await Assert.That(h.Source.ContentRequested.Count).IsEqualTo(2);
        });
    }

    [Test]
    public async Task A_superseded_load_does_not_overwrite_the_newer_one() {
        var h = new Harness();
        var slow = h.Source.ContentGate();
        h.Source.EnqueueContent(new PageContentRead.Found(Page("newer"), 4, "<p>new</p>"));

        var first = h.Vm.LoadAsync(CancellationToken.None);
        await h.Vm.LoadAsync(CancellationToken.None);
        slow.SetResult(new PageContentRead.Found(Page("older"), 3, "<p>old</p>"));
        await first;

        await Assert.That(h.Vm.Title).IsEqualTo("newer");
        await Assert.That(h.Vm.Html).Contains("new");
    }

    [Test]
    [Arguments("about:blank")]
    [Arguments("about:srcdoc")]
    public async Task The_host_load_and_the_frame_document_navigate_in_place(string url) {
        var h = new Harness();

        await Assert.That(h.Vm.CancelNavigation(new Uri(url))).IsFalse();
        await Assert.That(h.Opener.Opened).IsEmpty();
    }

    [Test]
    public async Task Any_other_navigation_is_cancelled_and_offered_to_the_browser() {
        var h = new Harness();

        await Assert.That(h.Vm.CancelNavigation(new Uri("https://example.test/a"))).IsTrue();
        await Assert.That(h.Vm.CancelNavigation(new Uri("http://localhost:12345/"))).IsTrue();
        await Assert.That(h.Vm.CancelNavigation(new Uri("file:///etc/passwd"))).IsTrue();
        await Assert.That(h.Vm.CancelNavigation(null)).IsTrue();

        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://example.test/a", "http://localhost:12345/" });
    }

    [Test]
    public async Task A_new_window_never_opens_in_app_and_goes_to_the_browser() {
        var h = new Harness();

        h.Vm.OpenNewWindow(new Uri("https://example.test/popup"));
        h.Vm.OpenNewWindow(new Uri("javascript:alert(1)"));

        await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://example.test/popup" });
    }
}
