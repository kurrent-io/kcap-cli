using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateCommandBundledTests {
    [Test]
    public async Task Check_json_reports_not_newer_and_the_app_install_tag() {
        var json = JsonNode.Parse(UpdateCommand.BundledCheckJson("0.12.0-beta.2", "beta"))!.AsObject();

        await Assert.That(json["current"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["channel"]!.GetValue<string>()).IsEqualTo("beta");
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("app");
    }

    [Test]
    public async Task Bundled_message_names_the_app_and_its_menu_item() {
        await Assert.That(UpdateCommand.BundledMessage).Contains("kcap");
        await Assert.That(UpdateCommand.BundledMessage).Contains("Check for Updates…");
    }

    [Test]
    public async Task Check_json_carries_the_snap_install_tag() {
        var json = JsonNode.Parse(UpdateCommand.BundledCheckJson("1.4.0", "latest", "snap"))!.AsObject();

        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("snap");
    }

    [Test]
    public async Task Snap_message_points_at_snap_refresh() {
        await Assert.That(UpdateCommand.SnapMessage(new SnapLayout("kcap", "42"))).EndsWith("sudo snap refresh kcap");
        await Assert.That(UpdateCommand.SnapMessage(null)).EndsWith("sudo snap refresh");
    }

    /// <summary>snapd never refreshes a `--dangerous` install, so pointing it at `snap refresh` would be wrong.</summary>
    [Test]
    public async Task A_local_snap_is_pointed_at_a_new_snap_file() {
        var message = UpdateCommand.SnapMessage(new SnapLayout("kcap", "x3"));

        await Assert.That(message).Contains("revision x3");
        await Assert.That(message).Contains("sudo snap install --dangerous --classic");
        await Assert.That(message).DoesNotContain("snap refresh");
    }
}
