using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class OnlyServerPreconditionTests {
    const string Local = "http://127.0.0.1:9473";

    static ProfileConfig With(params (string Name, string? Url)[] profiles) =>
        ProfileConfig.Fresh() with {
            Profiles = profiles.ToDictionary(p => p.Name, p => new Profile { ServerUrl = p.Url! }),
        };

    [Test]
    public async Task OnlyServer_refuses_a_profile_for_another_server() {
        var config = With(("default", Local), ("work", "https://kcap.example.com"));

        var ex = Assert.Throws<CommitPreconditionFailedException>(() => new CommitPrecondition.OnlyServer(Local).Check(config, "default"));

        await Assert.That(ex.Message).Contains("profile 'work' points at https://kcap.example.com");
    }

    [Test]
    public async Task OnlyServer_admits_blank_and_same_server_profiles() {
        var config = With(("default", Local), ("blank", ""), ("again", Local + "/"));

        await Assert.That(() => new CommitPrecondition.OnlyServer(Local).Check(config, "default")).ThrowsNothing();
    }

    static readonly LocalEndpoints Installation = new(new StateRead<InstallRegistry>.Present(
        InstallRegistry.Empty.WithActive(new("inst", 9473, "http://localhost:9473", "1.2.3"))));

    static CommitPrecondition.OnlyServer LocalOnly => new("http://localhost:9473", Installation.Canonicalize);

    [Test]
    [Arguments("http://127.0.0.1:9473")]
    [Arguments("http://[::1]:9473")]
    [Arguments("http://127.0.0.1:9473/")]
    public async Task OnlyServer_admits_a_loopback_spelling_of_the_installation(string alias) {
        var config = With(("default", "http://localhost:9473"), ("alias", alias));

        await Assert.That(() => LocalOnly.Check(config, "default")).ThrowsNothing();
    }

    [Test]
    [Arguments("http://127.0.0.1:9474")]
    [Arguments("http://[::1]:9474")]
    [Arguments("https://127.0.0.1:9473")]
    [Arguments("http://kcap.example.com:9473")]
    public async Task OnlyServer_refuses_another_port_scheme_or_host_despite_the_installation(string other) {
        var config = With(("default", "http://localhost:9473"), ("other", other));

        var ex = Assert.Throws<CommitPreconditionFailedException>(() => LocalOnly.Check(config, "default"));

        await Assert.That(ex.Message).Contains($"profile 'other' points at {other}");
    }

    [Test]
    public async Task OnlyServer_without_an_installation_compares_loopback_spellings_literally() {
        var config = With(("default", "http://127.0.0.1:9473"));

        await Assert.That(() => new CommitPrecondition.OnlyServer("http://localhost:9473").Check(config, "default"))
            .Throws<CommitPreconditionFailedException>();
    }
}
