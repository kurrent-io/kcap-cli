using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>DaemonCommands.ApplySpawnEnvironment</c> is what <c>kcap daemon start</c> (foreground and
/// <c>-d</c>) overlays onto the spawned daemon's <c>ProcessStartInfo.Environment</c> — the daemon/config
/// roots, plus the same Antigravity ADC trio derivation <c>ServiceEnvironment.Capture</c> runs at
/// service install, so a daemon started any other way is not left without it.
/// </summary>
public class DaemonSpawnEnvironmentTests {
    [Test]
    public async Task Includes_the_trio_when_adc_and_project_exist() {
        var env = new Dictionary<string, string>();

        DaemonCommands.ApplySpawnEnvironment(
            env, "/daemons", "/config", isWindows: false,
            adcCredentialsPath: "/h/.config/gcloud/application_default_credentials.json",
            gcloudProject: "gcloud-proj");

        await Assert.That(env["GOOGLE_APPLICATION_CREDENTIALS"])
            .IsEqualTo("/h/.config/gcloud/application_default_credentials.json");
        await Assert.That(env["AGY_ADC_AUTH"]).IsEqualTo("1");
        await Assert.That(env["GOOGLE_CLOUD_PROJECT"]).IsEqualTo("gcloud-proj");
    }

    [Test]
    public async Task Omits_the_trio_without_adc_or_project() {
        var env = new Dictionary<string, string>();

        DaemonCommands.ApplySpawnEnvironment(
            env, "/daemons", "/config", isWindows: false,
            adcCredentialsPath: null, gcloudProject: null);

        await Assert.That(env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS")).IsFalse();
        await Assert.That(env.ContainsKey("AGY_ADC_AUTH")).IsFalse();
        await Assert.That(env.ContainsKey("GOOGLE_CLOUD_PROJECT")).IsFalse();
    }

    [Test]
    public async Task Never_derives_on_windows_even_when_adc_and_project_are_given() {
        var env = new Dictionary<string, string>();

        DaemonCommands.ApplySpawnEnvironment(
            env, "/daemons", "/config", isWindows: true,
            adcCredentialsPath: "/derived/adc.json", gcloudProject: "gcloud-proj");

        await Assert.That(env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS")).IsFalse();
        await Assert.That(env.ContainsKey("AGY_ADC_AUTH")).IsFalse();
        await Assert.That(env.ContainsKey("GOOGLE_CLOUD_PROJECT")).IsFalse();
    }

    [Test]
    public async Task Keeps_an_already_exported_credentials_path() {
        var env = new Dictionary<string, string> { ["GOOGLE_APPLICATION_CREDENTIALS"] = "/custom/adc.json" };

        DaemonCommands.ApplySpawnEnvironment(
            env, "/daemons", "/config", isWindows: false,
            adcCredentialsPath: "/derived/adc.json", gcloudProject: null);

        await Assert.That(env["GOOGLE_APPLICATION_CREDENTIALS"]).IsEqualTo("/custom/adc.json");
    }

    [Test]
    public async Task Always_carries_the_daemon_and_config_roots() {
        var env = new Dictionary<string, string>();

        DaemonCommands.ApplySpawnEnvironment(
            env, "/daemons", "/config", isWindows: false,
            adcCredentialsPath: null, gcloudProject: null);

        await Assert.That(env[DaemonStore.DaemonsDirEnvVar]).IsEqualTo("/daemons");
        await Assert.That(env[ConfigRoot.ConfigDirEnvVar]).IsEqualTo("/config");
    }
}
