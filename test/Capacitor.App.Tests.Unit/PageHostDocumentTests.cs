using System.Net;
using System.Text.RegularExpressions;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The host document's shape: the page in a sandboxed srcdoc frame under the server's content
/// policy, and the nonce only inside the host script.
public class PageHostDocumentTests {
    const string Nonce = "Zm9vYmFyYmF6cXV4LXF1dXgtY29yZ2UtZ3JhdWx0LWdhcnBseQ";

    static string Srcdoc(string host) {
        var match = Regex.Match(host, "srcdoc=\"([^\"]*)\"");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Test]
    public async Task The_srcdoc_escapes_quotes_and_ampersands() {
        var host = PageHostDocument.Build("<p class=\"x\">Tom & Jerry &quot;</p>", Nonce);

        await Assert.That(host).Contains("&lt;p class=&quot;x&quot;&gt;Tom &amp; Jerry &amp;quot;&lt;/p&gt;");
        await Assert.That(Srcdoc(host)).EndsWith("<p class=\"x\">Tom & Jerry &quot;</p>");
    }

    [Test]
    public async Task The_frame_is_sandboxed_without_same_origin() {
        var host = PageHostDocument.Build("<p>x</p>", Nonce);

        await Assert.That(host).Contains("sandbox=\"allow-scripts allow-popups\"");
        await Assert.That(host).DoesNotContain("allow-same-origin");
        await Assert.That(host).DoesNotContain("allow-top-navigation");
    }

    [Test]
    public async Task The_page_starts_with_the_content_policy() {
        var srcdoc = Srcdoc(PageHostDocument.Build("<p>x</p>", Nonce));

        await Assert.That(srcdoc).StartsWith($"<meta http-equiv=\"Content-Security-Policy\" content=\"{PageHostDocument.ContentPolicy}\">");
        await Assert.That(PageHostDocument.ContentPolicy).Contains("connect-src 'none'");
        await Assert.That(PageHostDocument.ContentPolicy).Contains("default-src 'none'");
        await Assert.That(PageHostDocument.ContentPolicy).DoesNotContain("frame-ancestors");
        await Assert.That(PageHostDocument.ContentPolicy).DoesNotContain("sandbox");
    }

    [Test]
    [Arguments("<!doctype html><html><head><script>x</script></head></html>")]
    [Arguments("\u00a0<!doctype html><script>x</script>")]
    [Arguments("\ufeff<!doctype html><script>x</script>")]
    [Arguments("<!-- c --><!doctype html><script>x</script>")]
    public async Task The_policy_precedes_everything_the_page_wrote(string page) {
        var srcdoc = Srcdoc(PageHostDocument.Build(page, Nonce));

        await Assert.That(srcdoc).StartsWith("<meta http-equiv=\"Content-Security-Policy\"");
        await Assert.That(srcdoc).EndsWith(page);
    }

    [Test]
    public async Task The_host_carries_the_content_policy_and_lets_the_frame_ask_to_navigate() {
        var host = PageHostDocument.Build("<p>x</p>", Nonce);

        await Assert.That(host).Contains($"<meta http-equiv=\"Content-Security-Policy\" content=\"{PageHostDocument.HostPolicy}\">");
        await Assert.That(PageHostDocument.HostPolicy).StartsWith(PageHostDocument.ContentPolicy + ";");
        await Assert.That(PageHostDocument.HostPolicy).EndsWith("frame-src http: https: mailto:");
        await Assert.That(host.IndexOf(PageHostDocument.HostPolicy, StringComparison.Ordinal)).IsLessThan(host.IndexOf("<script>", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_nonce_is_in_the_host_script_only() {
        var host = PageHostDocument.Build("<p>x</p>", Nonce);

        await Assert.That(Srcdoc(host)).DoesNotContain(Nonce);
        var script = Regex.Match(host, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        await Assert.That(script).Contains($"\"{Nonce}\"");
        await Assert.That(Regex.Count(host, Regex.Escape(Nonce))).IsEqualTo(1);
    }

    [Test]
    public async Task The_host_script_runs_before_the_frame_exists() {
        var host = PageHostDocument.Build("<p>x</p>", Nonce);

        await Assert.That(host.IndexOf("</script>", StringComparison.Ordinal)).IsLessThan(host.IndexOf("<iframe", StringComparison.Ordinal));
        await Assert.That(host).DoesNotContain("__KCAP_NONCE__");
    }

    [Test]
    [Arguments("")]
    [Arguments("abc\"); alert(1); (\"")]
    [Arguments("abc def")]
    public async Task A_nonce_that_is_not_base64url_is_refused(string nonce) {
        await Assert.That(() => PageHostDocument.Build("<p>x</p>", nonce)).Throws<ArgumentException>();
    }

    [Test]
    public async Task A_fresh_nonce_is_32_random_bytes_in_base64url() {
        var a = PageHostDocument.NewNonce();
        var b = PageHostDocument.NewNonce();

        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(a.Length).IsEqualTo(43);
        await Assert.That(Regex.IsMatch(a, "^[A-Za-z0-9_-]+$")).IsTrue();
    }
}
