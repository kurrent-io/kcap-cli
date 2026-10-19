namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>What a hub connection sends on negotiation: the version, and the update-check opt-out only when set.</summary>
public class HttpClientExtensionsObservationHeadersTests {
    [Test]
    public async Task The_opt_out_rides_with_the_version() {
        var headers = new Dictionary<string, string>();

        HttpClientExtensions.AddObservationHeaders(headers, updateCheckOff: true);

        await Assert.That(headers).IsEquivalentTo(new Dictionary<string, string> {
            [HttpClientExtensions.CliVersionHeader]  = HttpClientExtensions.CliVersion!,
            [HttpClientExtensions.UpdateCheckHeader] = HttpClientExtensions.UpdateCheckOffValue,
        });
    }

    [Test]
    public async Task An_update_check_left_on_sends_only_the_version() {
        var headers = new Dictionary<string, string>();

        HttpClientExtensions.AddObservationHeaders(headers, updateCheckOff: false);

        await Assert.That(headers.Keys).IsEquivalentTo([HttpClientExtensions.CliVersionHeader]);
    }
}
