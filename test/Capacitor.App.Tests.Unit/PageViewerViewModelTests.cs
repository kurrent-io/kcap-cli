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
        await Assert.That(h.Vm.Html).Contains("&lt;p class=&quot;x&quot;&gt;Tom &amp; Jerry&lt;/p&gt;\"");
        await Assert.That(h.Source.ContentRequested).IsEquivalentTo(new[] { (Id, (int?)null) });
    }

    [Test]
    public async Task Every_load_binds_a_fresh_nonce_into_its_host_document() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p>a</p>"), new PageContentRead.Found(Page(), 3, "<p>a</p>"));

        await h.Vm.LoadAsync(CancellationToken.None);
        var first = h.Vm.Nonce;
        await Assert.That(first).IsNotNull();
        await Assert.That(h.Vm.Html).Contains(first!);

        await h.Vm.LoadAsync(CancellationToken.None);
        await Assert.That(h.Vm.Nonce).IsNotEqualTo(first);
        await Assert.That(h.Vm.Html).Contains(h.Vm.Nonce!);
        await Assert.That(h.Vm.Html).DoesNotContain(first!);
    }

    [Test]
    public async Task A_found_page_reads_results_access_before_it_is_ready() {
        var h = new Harness();
        h.Source.Access = PageResultsAccess.Hidden;
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p>a</p>"));

        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Source.AccessChecks).IsEqualTo(1);
        await Assert.That(h.Vm.Access).IsEqualTo(PageResultsAccess.Hidden);
    }

    [Test]
    public async Task The_bridge_answers_for_the_loaded_page_and_nonce() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p>a</p>"));
        await h.Vm.LoadAsync(CancellationToken.None);
        var posts = new List<string>();

        await using var bridge = h.Vm.CreateBridge(json => { posts.Add(json); return Task.CompletedTask; })!;
        await bridge.OnNativeMessageAsync($$$"""{"nonce":"{{{h.Vm.Nonce}}}","data":{"kcap":"v1","type":"ready"}}""");

        await Assert.That(posts.Count).IsGreaterThan(0);
        await Assert.That(h.Source.BridgeRequested[0]).IsEqualTo(("mine", Id, 3, (System.Text.Json.JsonElement?)null));
    }

    [Test]
    public async Task No_bridge_exists_without_a_loaded_page() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.NotFound());
        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Vm.CreateBridge(_ => Task.CompletedTask)).IsNull();
        await Assert.That(h.Vm.Nonce).IsNull();
    }

    [Test]
    public async Task An_unusable_webview_drops_the_page_and_falls_back_to_the_browser() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p>a</p>"));
        await h.Vm.LoadAsync(CancellationToken.None);

        h.Vm.WebViewUnusable();

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.NoWebView);
        await Assert.That(h.Vm.Html).IsNull();
        await Assert.That(h.Vm.CreateBridge(_ => Task.CompletedTask)).IsNull();
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
    public async Task A_cancelled_navigation_opens_nothing_and_waits_for_a_click() {
        var h = new Harness();

        await Assert.That(h.Vm.CancelNavigation(new Uri("https://example.test/a"))).IsTrue();

        await Assert.That(h.Opener.Opened).IsEmpty();
        await Assert.That(h.Vm.PendingExternalUrl).IsEqualTo("https://example.test/a");
        await Assert.That(h.Vm.PendingPrompt).IsEqualTo("This page wants to open example.test");
    }

    [Test]
    public async Task The_latest_navigation_is_the_pending_one() {
        var h = new Harness();

        h.Vm.CancelNavigation(new Uri("https://example.test/a"));
        h.Vm.CancelNavigation(new Uri("https://other.test/b"));

        await Assert.That(h.Vm.PendingExternalUrl).IsEqualTo("https://other.test/b");
    }

    [Test]
    public async Task Unopenable_targets_never_become_pending() {
        var h = new Harness();

        await Assert.That(h.Vm.CancelNavigation(new Uri("javascript:alert(1)"))).IsTrue();
        await Assert.That(h.Vm.CancelNavigation(new Uri("file:///etc/passwd"))).IsTrue();
        await Assert.That(h.Vm.CancelNavigation(null)).IsTrue();
        h.Vm.OpenNewWindow(new Uri("data:text/html,x"));

        await Assert.That(h.Vm.PendingExternalUrl).IsNull();
        await Assert.That(h.Opener.Opened).IsEmpty();
    }

    [Test]
    public async Task A_new_window_request_is_held_like_a_navigation() {
        var h = new Harness();

        h.Vm.OpenNewWindow(new Uri("https://example.test/popup"));

        await Assert.That(h.Opener.Opened).IsEmpty();
        await Assert.That(h.Vm.PendingExternalUrl).IsEqualTo("https://example.test/popup");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_sends_the_pending_link_once_and_clears_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Vm.CancelNavigation(new Uri("https://example.test/a"));

            await h.Vm.OpenPendingCommand.Execute();
            await h.Vm.OpenPendingCommand.Execute();

            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://example.test/a" });
            await Assert.That(h.Vm.PendingExternalUrl).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dismiss_clears_the_pending_link_without_opening_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Vm.CancelNavigation(new Uri("https://example.test/a"));

            await h.Vm.DismissPendingCommand.Execute();

            await Assert.That(h.Opener.Opened).IsEmpty();
            await Assert.That(h.Vm.PendingExternalUrl).IsNull();
        });
    }

    [Test]
    public async Task A_new_load_clears_the_pending_link() {
        var h = new Harness();
        h.Vm.CancelNavigation(new Uri("https://example.test/a"));

        await h.Vm.LoadAsync(CancellationToken.None);

        await Assert.That(h.Vm.PendingExternalUrl).IsNull();
    }

    [Test]
    public async Task A_cancelled_first_load_does_not_stay_loading() {
        var h = new Harness();
        h.Source.ContentGate();
        using var cts = new CancellationTokenSource();

        var load = h.Vm.LoadAsync(cts.Token);
        cts.Cancel();
        await load;

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Unreachable);
    }

    [Test]
    public async Task A_cancelled_reload_keeps_the_page_already_shown() {
        var h = new Harness();
        h.Source.EnqueueContent(new PageContentRead.Found(Page(), 3, "<p>v3</p>"));
        await h.Vm.LoadAsync(CancellationToken.None);
        h.Source.ContentGate();
        using var cts = new CancellationTokenSource();

        var load = h.Vm.LoadAsync(cts.Token);
        cts.Cancel();
        await load;

        await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Ready);
        await Assert.That(h.Vm.Html).Contains("v3");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failing_reload_lands_in_unreachable() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.ContentGate().SetException(new InvalidOperationException("boom"));

            await h.Vm.ReloadCommand.Execute();

            await Assert.That(h.Vm.State).IsEqualTo(PageViewerState.Unreachable);
        });
    }
}
