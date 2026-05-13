using kapacitor.Config;

namespace kapacitor.Tests.Unit.Config;

public class ServerUrlNormalizerLoopbackTests {
    [Test]
    [Arguments("localhost", "http://localhost")]
    [Arguments("localhost:5108", "http://localhost:5108")]
    [Arguments("127.0.0.1", "http://127.0.0.1")]
    [Arguments("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [Arguments("::1", "http://::1")]
    [Arguments("host.docker.internal", "http://host.docker.internal")]
    [Arguments("host.docker.internal:5108", "http://host.docker.internal:5108")]
    public async Task Loopback_HostsGetHttp(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("staging.kapacitor.ai", "https://staging.kapacitor.ai")]
    [Arguments("staging.kapacitor.ai:8443", "https://staging.kapacitor.ai:8443")]
    [Arguments("example.com/api", "https://example.com/api")]
    public async Task NonLoopback_HostsGetHttps(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://staging.kapacitor.ai/", "https://staging.kapacitor.ai")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    [Arguments("https://example.com", "https://example.com")]
    public async Task ExistingScheme_IsPreservedAndTrimmed(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }
}
