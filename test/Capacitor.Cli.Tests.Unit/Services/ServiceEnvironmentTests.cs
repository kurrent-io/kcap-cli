using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceEnvironmentTests {
    [Test]
    public async Task Build_pins_profile_and_includes_path() {
        var src = new Dictionary<string, string> {
            ["PATH"]              = "/usr/local/bin:/usr/bin",
            ["KCAP_CONFIG_DIR"]   = "/home/u/.config/kcap",
            ["IRRELEVANT"]        = "x",
        };
        var env = ServiceEnvironment.Build(profileName: "work", source: src);
        await Assert.That(env["PATH"]).IsEqualTo("/usr/local/bin:/usr/bin");
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(env["KCAP_CONFIG_DIR"]).IsEqualTo("/home/u/.config/kcap");
        await Assert.That(env.ContainsKey("IRRELEVANT")).IsFalse();
    }

    [Test]
    public async Task Build_omits_profile_when_null_and_keeps_kcap_url() {
        var src = new Dictionary<string, string> { ["KCAP_URL"] = "https://x" };
        var env = ServiceEnvironment.Build(profileName: null, source: src);
        await Assert.That(env.ContainsKey("KCAP_PROFILE")).IsFalse();
        await Assert.That(env["KCAP_URL"]).IsEqualTo("https://x");
    }

    [Test]
    public async Task Build_explicit_profile_overrides_source_env() {
        var src = new Dictionary<string, string> { ["KCAP_PROFILE"] = "old" };
        var env = ServiceEnvironment.Build(profileName: "new", source: src);
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("new");
    }
}
