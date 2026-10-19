using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>How resolution behaves on a machine with a local installation.</summary>
public class ProfileResolverLocalTests {
    static readonly LocalEndpoints Installed = new(new StateRead<InstallRegistry>.Present(
        new InstallRegistry(new LocalInstallation("inst-1", 51234, "http://localhost:51234", "1.4.0"), [])));

    static ProfileConfig LocalConfig(string serverUrl = "http://localhost:51234") => new() {
        ActiveProfile = "default",
        Profiles      = new Dictionary<string, Profile> { ["default"] = new() { ServerUrl = serverUrl } },
    };

    static ResolvedProfile Resolve(
            ProfileConfig config, LocalEndpoints local, string? envUrl = null, RepoConfig? repoConfig = null) =>
        new ProfileResolver(config, cliServerUrl: null, envUrl, envProfile: null, repoConfig, repoRemoteUrls: [], repoPath: null, local)
            .Resolve();

    [Test]
    public async Task A_profile_spelling_the_installation_another_way_resolves_to_its_canonical_url() {
        var result = Resolve(LocalConfig("http://127.0.0.1:51234"), Installed);

        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
    }

    /// <summary>Hosted agents and launcher MCP configurations reach the server through KCAP_URL; they
    /// must land on the same identity as the profile.</summary>
    [Test]
    public async Task A_url_override_naming_the_installation_is_canonicalized_too() {
        var result = Resolve(LocalConfig(), Installed, envUrl: "http://[::1]:51234");

        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
    }

    [Test]
    public async Task A_repository_binding_to_a_missing_profile_is_ignored_for_the_installation() {
        var result = Resolve(LocalConfig(), Installed, repoConfig: new RepoConfig { Profile = "team", ServerUrl = "https://team.example" });

        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
        await Assert.That(result.ProfileName).IsEqualTo("default");
        await Assert.That(result.IgnoredRepoProfile).IsEqualTo("team");
        await Assert.That(result.Warning).IsNull();
    }

    /// <summary>Status says the recording goes to the installation, so the fallback may not land on a
    /// cloud profile that the ordinary precedence would pick.</summary>
    [Test]
    public async Task An_ignored_repository_binding_never_falls_through_to_a_cloud_profile() {
        var config = new ProfileConfig {
            ActiveProfile = "cloud",
            Profiles = new Dictionary<string, Profile> {
                ["cloud"] = new() { ServerUrl = "https://cloud.example" },
                ["local"] = new() { ServerUrl = "http://127.0.0.1:51234" },
            },
        };

        var result = Resolve(config, Installed, repoConfig: new RepoConfig { Profile = "team" });

        await Assert.That(result.ProfileName).IsEqualTo("local");
        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
    }

    [Test]
    public async Task With_no_profile_naming_it_an_ignored_binding_goes_to_the_installation_url() {
        var result = Resolve(LocalConfig("https://cloud.example"), Installed, repoConfig: new RepoConfig { Profile = "team" });

        await Assert.That(result.ProfileName).IsNull();
        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
    }

    [Test]
    public async Task Without_an_installation_a_missing_repository_profile_still_records_nothing() {
        var result = Resolve(LocalConfig("https://cloud.example"), LocalEndpoints.None,
            repoConfig: new RepoConfig { Profile = "team", ServerUrl = "https://team.example" });

        await Assert.That(result.ServerUrl).IsNull();
        await Assert.That(result.IgnoredRepoProfile).IsNull();
        await Assert.That(result.Warning).Contains("team");
    }

    [Test]
    public async Task A_repository_binding_to_a_profile_that_exists_is_still_honoured() {
        var config = LocalConfig();
        config.Profiles["team"] = new() { ServerUrl = "http://127.0.0.1:51234" };

        var result = Resolve(config, Installed, repoConfig: new RepoConfig { Profile = "team" });

        await Assert.That(result.ProfileName).IsEqualTo("team");
        await Assert.That(result.ServerUrl).IsEqualTo("http://localhost:51234");
        await Assert.That(result.IgnoredRepoProfile).IsNull();
    }

    [Test]
    public async Task A_resolution_naming_the_installation_sends_no_bearer() {
        var result = Resolve(LocalConfig("http://[::1]:51234"), Installed);

        await Assert.That(result.Endpoint).IsTypeOf<LocalEndpointClass.Local>();
        await Assert.That(result.SendsNoBearer).IsTrue();
    }

    [Test]
    public async Task A_cloud_resolution_on_the_same_machine_still_sends_one() {
        var result = Resolve(LocalConfig("https://cloud.example"), Installed);

        await Assert.That(result.Endpoint).IsTypeOf<LocalEndpointClass.Ordinary>();
        await Assert.That(result.SendsNoBearer).IsFalse();
    }

    /// <summary>Setup points a resolution computed before it chose a server at the one it chose; the
    /// classification must follow the URL in both directions.</summary>
    [Test]
    public async Task Retargeting_reclassifies_rather_than_carrying_the_old_answer() {
        var local = Resolve(LocalConfig(), Installed);
        var cloud = Resolve(LocalConfig("https://cloud.example"), Installed);

        await Assert.That(local.Retargeted("https://cloud.example", Installed).SendsNoBearer).IsFalse();

        var toLocal = cloud.Retargeted("http://127.0.0.1:51234", Installed);
        await Assert.That(toLocal.SendsNoBearer).IsTrue();
        await Assert.That(toLocal.ServerUrl).IsEqualTo("http://localhost:51234");
    }
}
