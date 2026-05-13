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

public class ServerUrlNormalizerOrchestrationTests {
    static Func<string, TimeSpan, CancellationToken, Task<bool>> Probe(Func<string, bool> reachable) =>
        (url, _, _) => Task.FromResult(reachable(url));

    [Test]
    public async Task SkipProbe_ReturnsLoopbackDefault_NoWarning_NoProbeCalls() {
        var probeCalls = 0;
        var probe = Probe(u => { probeCalls++; return true; });

        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: true, CancellationToken.None, probe);

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SchemePresent_ProbeSucceeds_NoWarning() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kapacitor.ai/", skipProbe: false, CancellationToken.None,
            Probe(_ => true));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task SchemePresent_ProbeFails_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
    }

    [Test]
    public async Task SchemeMissing_HttpsSucceeds_UsesHttps() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("https://")); });

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls[0]).IsEqualTo("https://staging.kapacitor.ai");
    }

    [Test]
    public async Task SchemeMissing_HttpsFails_HttpSucceeds_UsesHttp() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("http://")); });

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls.Count).IsEqualTo(2);
        await Assert.That(probedUrls[0]).StartsWith("https://");
        await Assert.That(probedUrls[1]).StartsWith("http://");
    }

    [Test]
    public async Task SchemeMissing_BothFail_FallsBackToLoopbackDefault_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kapacitor.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kapacitor.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
    }

    [Test]
    public async Task SchemeMissing_BothFail_Loopback_UsesHttpFallback() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNotNull();
    }
}
