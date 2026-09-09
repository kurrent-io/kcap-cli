namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Which <c>KCAP_DAEMON_URL</c> values a hook may post a tool payload to, and which of the two
/// refusals each rejection is: nothing named, or named and off-loopback. Callers act on that
/// difference, so a rejection collapsing to "no bridge" would silently change what they do.
/// </summary>
public class DaemonBridgeTests {
    [Test]
    public async Task Loopback_http_is_accepted() =>
        await Assert.That(DaemonBridge.Parse("http://127.0.0.1:54321/abc123"))
                    .IsEqualTo(new DaemonBridge.Loopback("http://127.0.0.1:54321/abc123"));

    [Test]
    public async Task A_trailing_slash_is_stripped_so_callers_append_their_own_route() =>
        await Assert.That(DaemonBridge.Parse("http://127.0.0.1:54321/abc123/"))
                    .IsEqualTo(new DaemonBridge.Loopback("http://127.0.0.1:54321/abc123"));

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Nothing_named_is_not_a_refusal(string? raw) =>
        await Assert.That(DaemonBridge.Parse(raw)).IsEqualTo(DaemonBridge.None);

    [Test]
    [Arguments("https://127.0.0.1:54321/abc123")]
    [Arguments("http://example.com:54321/abc123")]
    [Arguments("http://localhost:54321/abc123")]
    [Arguments("http://[::1]:54321/abc123")]
    [Arguments("not-a-url")]
    public async Task An_address_off_loopback_is_refused_and_reported_verbatim(string raw) =>
        await Assert.That(DaemonBridge.Parse(raw)).IsEqualTo(new DaemonBridge.NotLoopback(raw));
}
