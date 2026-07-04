using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end verification that <c>UpdateCommand</c>'s channel-aware check
/// queries the right npm dist-tag. Points <see cref="UpdateCommand.RegistryBaseUrl"/>
/// at a WireMock-stubbed registry instead of the real <c>registry.npmjs.org</c>,
/// mirroring the harness in <see cref="Config.ServerUrlProbeIntegrationTests"/>.
/// The config dir (and therefore the per-channel update-check cache) is isolated
/// by <see cref="IntegrationGlobalSetup"/>, which pins <c>KCAP_CONFIG_DIR</c>
/// before any test runs.
/// </summary>
public class UpdateChannelQueryTests : IDisposable {
    readonly WireMockServer _server         = WireMockServer.Start();
    readonly string         _originalBaseUrl = UpdateCommand.RegistryBaseUrl;

    public UpdateChannelQueryTests() {
        UpdateCommand.RegistryBaseUrl = _server.Url!;

        _server.Given(Request.Create().WithPath("/@kurrent/kcap/latest").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.8.0"}"""));

        _server.Given(Request.Create().WithPath("/@kurrent/kcap/beta").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.9.0-beta.1"}"""));
    }

    public void Dispose() {
        UpdateCommand.RegistryBaseUrl = _originalBaseUrl;
        _server.Stop();
    }

    // UpdateCommand.RegistryBaseUrl is a shared static seam; serialize both
    // tests in this class so they don't race each other's WireMock server.
    [Test, NotInParallel("UpdateCommand_RegistryBaseUrl")]
    public async Task Beta_channel_reports_beta_dist_tag_version() {
        var (latest, _) = await UpdateCommand.CheckForUpdateAsync(forceCheck: true, "beta");

        await Assert.That(latest).IsEqualTo("0.9.0-beta.1");

        var hits = _server.FindLogEntries(Request.Create().WithPath("/@kurrent/kcap/beta").UsingGet());
        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test, NotInParallel("UpdateCommand_RegistryBaseUrl")]
    public async Task Latest_channel_reports_latest_dist_tag_version() {
        var (latest, _) = await UpdateCommand.CheckForUpdateAsync(forceCheck: true, "latest");

        await Assert.That(latest).IsEqualTo("0.8.0");

        var hits = _server.FindLogEntries(Request.Create().WithPath("/@kurrent/kcap/latest").UsingGet());
        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(1);
    }
}
