using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The <c>kcap update --check</c> JSON the npm launcher installs from: a server that trails npm latest
/// pins <c>install_tag</c> to its own version, so <c>kcap update</c> never installs past it.
/// </summary>
public class UpdateCommandCheckJsonTests {
    static JsonObject Check(string? current, string? latest, bool newer, string channel, string? serverVersion) {
        var result   = new UpdateCommand.UpdateCheckResult(current, latest, newer, FromCache: false);
        var advisory = UpdateAdvisoryResolver.Resolve(result, channel, serverVersion);

        return JsonNode.Parse(UpdateCommand.CheckJson(advisory, channel))!.AsObject();
    }

    [Test]
    public async Task A_server_behind_npm_latest_pins_the_install_to_its_version() {
        var json = Check("1.0.0", "1.0.2", newer: true, "latest", serverVersion: "1.0.1");

        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("1.0.1");
        await Assert.That(json["newer"]!.GetValue<bool>()).IsTrue();
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("1.0.1");
        await Assert.That(json["channel"]!.GetValue<string>()).IsEqualTo("latest");
    }

    [Test]
    public async Task A_cli_at_the_server_version_is_up_to_date_although_npm_has_newer() {
        var json = Check("1.0.1", "1.0.2", newer: true, "latest", serverVersion: "1.0.1");

        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Without_a_known_server_version_the_channel_tag_is_installed() {
        var json = Check("1.0.1", "1.0.2", newer: true, "latest", serverVersion: null);

        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("1.0.2");
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("latest");
    }

    [Test]
    public async Task The_beta_channel_is_never_capped() {
        var json = Check("1.0.1", "1.1.0-beta.1", newer: true, "beta", serverVersion: "1.0.1");

        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("1.1.0-beta.1");
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("beta");
    }

    [Test]
    public async Task An_unknown_latest_reports_newer_as_null_not_false() {
        var json = Check("1.0.1", latest: null, newer: false, "latest", serverVersion: "1.0.1");

        await Assert.That(json.ContainsKey("newer")).IsTrue();
        await Assert.That(json["newer"]).IsNull();
    }
}
