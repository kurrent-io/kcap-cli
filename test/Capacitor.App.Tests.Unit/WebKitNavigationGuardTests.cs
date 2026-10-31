using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit;

/// The URLs the guard cancels are exactly those System.Uri refuses, which WebKit hands the WebView
/// package's navigation delegate from a page's link and which it turns into a process-ending throw.
public class WebKitNavigationGuardTests {
    [Test]
    [Arguments("foo://exa%25mple/")]
    [Arguments("foo://a%zz/")]
    [Arguments("mailto:%")]
    [Arguments("mailto:a%zz")]
    [Arguments("foo://x:99999/")]
    [Arguments(null)]
    public async Task A_url_system_uri_refuses_does_not_parse(string? url) {
        await Assert.That(WebKitNavigationGuard.Parses(url)).IsFalse();
        if (url is not null) await Assert.That(() => new Uri(url)).Throws<UriFormatException>();
    }

    [Test]
    [Arguments("about:blank")]
    [Arguments("about:srcdoc")]
    [Arguments("https://example.com/a?b=c#d")]
    [Arguments("mailto:someone@example.com")]
    public async Task A_url_the_viewer_handles_parses(string url) {
        await Assert.That(WebKitNavigationGuard.Parses(url)).IsTrue();
    }

    [Test]
    public async Task Without_a_web_view_nothing_is_installed() {
        await Assert.That(WebKitNavigationGuard.TryInstall(0)).IsFalse();
    }
}
