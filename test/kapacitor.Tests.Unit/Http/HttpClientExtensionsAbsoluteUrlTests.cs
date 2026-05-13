namespace kapacitor.Tests.Unit.Http;

public class HttpClientExtensionsAbsoluteUrlTests {
    [Test]
    [Arguments("https://staging.kapacitor.ai/hooks/stop")]
    [Arguments("http://localhost:5108/hooks/stop")]
    [Arguments("http://127.0.0.1:5108")]
    public async Task Accepts_AbsoluteHttpAndHttps(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsTrue();
    }

    [Test]
    [Arguments("staging.kapacitor.ai/hooks/stop")]
    [Arguments("/hooks/stop")]
    [Arguments("")]
    [Arguments("not a url at all")]
    public async Task Rejects_RelativeOrMalformed(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("ftp://example.com")]
    [Arguments("javascript:alert(1)")]
    public async Task Rejects_NonHttpSchemes(string url) {
        await Assert.That(kapacitor.HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }
}
