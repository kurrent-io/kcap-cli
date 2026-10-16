using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class MuseAssistantEnvironmentTests {
    static readonly UserHome Hatch = new("/home/hatch");

    static Func<string, bool> Existing(params string[] dirs) => path => dirs.Contains(path);

    [Test]
    public async Task Detected_when_the_hatch_home_has_a_workspace_and_the_image_bin_is_present() {
        var exists = Existing("/home/hatch/workspace", "/opt/hatch-image/bin");

        await Assert.That(MuseAssistantEnvironment.IsMuseAssistant(Hatch, exists)).IsTrue();
    }

    [Test]
    public async Task Not_detected_under_another_home_even_with_the_image_bin() {
        var exists = Existing("/home/alice/workspace", "/opt/hatch-image/bin");

        await Assert.That(MuseAssistantEnvironment.IsMuseAssistant(new UserHome("/home/alice"), exists)).IsFalse();
    }

    [Test]
    public async Task Not_detected_without_the_workspace() {
        await Assert.That(MuseAssistantEnvironment.IsMuseAssistant(Hatch, Existing("/opt/hatch-image/bin"))).IsFalse();
    }

    [Test]
    public async Task Not_detected_without_the_image_bin() {
        await Assert.That(MuseAssistantEnvironment.IsMuseAssistant(Hatch, Existing("/home/hatch/workspace"))).IsFalse();
    }

    [Test]
    public async Task A_trailing_slash_on_the_home_still_matches() {
        var exists = Existing("/home/hatch/workspace", "/opt/hatch-image/bin");

        await Assert.That(MuseAssistantEnvironment.IsMuseAssistant(new UserHome("/home/hatch/"), exists)).IsTrue();
    }

    [Test]
    public async Task The_telemetry_host_is_the_posthog_proxy() =>
        await Assert.That(CliTelemetry.EndpointHost).IsEqualTo("phog.kurrent.io");
}
