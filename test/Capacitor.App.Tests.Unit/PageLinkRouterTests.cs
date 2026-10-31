using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Which links name a page on the profile's own server: scheme, host and port must match it exactly
/// before the path is read for an id.
public class PageLinkRouterTests {
    [Test]
    [Arguments("https://srv.test/pages/abc", "abc")]
    [Arguments("https://SRV.test:443/pages/abc", "abc")]
    [Arguments("https://srv.test/artefacts/abc", "abc")]
    [Arguments("https://evil.test/pages/abc", null)]
    [Arguments("http://srv.test/pages/abc", null)]
    [Arguments("https://srv.test.evil/pages/abc", null)]
    [Arguments("https://srv.test:8443/pages/abc", null)]
    [Arguments("https://srv.test@evil.test/pages/abc", null)]
    [Arguments("https://srv.test/agents/abc", null)]
    [Arguments("abc", null)]
    [Arguments("javascript:alert(1)", null)]
    public async Task A_link_names_a_page_only_on_the_profile_server(string url, string? expected) {
        var router = new PageLinkRouter(() => "https://srv.test");

        await Assert.That(router.PageIdFor(url)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_profile_switch_is_read_on_every_link() {
        var server = "https://srv.test";
        var router = new PageLinkRouter(() => server);
        await Assert.That(router.PageIdFor("https://other.test/pages/abc")).IsNull();

        server = "https://other.test";
        await Assert.That(router.PageIdFor("https://other.test/pages/abc")).IsEqualTo("abc");
    }

    [Test]
    public async Task No_profile_server_routes_nothing() {
        var router = new PageLinkRouter(() => null);

        await Assert.That(router.PageIdFor("https://srv.test/pages/abc")).IsNull();
    }
}
