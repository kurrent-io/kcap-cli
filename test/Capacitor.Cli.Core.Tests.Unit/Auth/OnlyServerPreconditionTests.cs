using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

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

    [Test]
    public async Task OnlyServer_admits_a_loopback_spelling_of_the_same_server() {
        var config = With(("default", "http://localhost:9473"));
        // SameServer compares hosts literally, so localhost and 127.0.0.1 currently differ.
        var same   = ServerIdentity.SameServer("http://localhost:9473", Local);

        if (same) await Assert.That(() => new CommitPrecondition.OnlyServer(Local).Check(config, "default")).ThrowsNothing();
        else await Assert.That(() => new CommitPrecondition.OnlyServer(Local).Check(config, "default")).Throws<CommitPreconditionFailedException>();
    }
}
