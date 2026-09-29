using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// The guard decides, under the profile's token lock, whether a credential may still be written.
/// Every field shaping it is an optional constructor argument, so a wrong default reads as a pass.
public class LoginTargetSaveGuardTests {
    const string ProfileName = "acme";
    const string Server      = "https://acme.kcap.ai";

    static LoginTarget Target(
            bool pointsAtServer, bool adoptServer, bool existedAtRead = true,
            CommitPrecondition? precondition = null) =>
        new(ProfileName, ServerIdentity.Canonicalize(Server)!, Server, pointsAtServer, adoptServer, existedAtRead,
            precondition);

    static ProfileConfig Naming(string? serverUrl) =>
        new() { Profiles = new() { [ProfileName] = new Profile { ServerUrl = serverUrl } } };

    static ProfileConfig WithoutTheProfile() =>
        new() { Profiles = new() { ["other"] = new Profile { ServerUrl = Server } } };

    [Test]
    public async Task A_foreign_login_for_a_profile_that_never_existed_saves_unguarded() {
        var target = Target(pointsAtServer: false, adoptServer: false, existedAtRead: false);

        await Assert.That(target.SaveGuard).IsNull();
    }

    [Test]
    public async Task A_foreign_login_for_a_profile_that_existed_requires_it_to_still_exist() {
        var guard = Target(pointsAtServer: false, adoptServer: false).SaveGuard;

        await Assert.That(guard).IsNotNull();
        await Assert.That(guard!(Naming("https://elsewhere.example"))).IsTrue();
        await Assert.That(guard(WithoutTheProfile())).IsFalse();
    }

    [Test]
    public async Task An_adopting_login_without_a_precondition_requires_existence_only() {
        var guard = Target(pointsAtServer: false, adoptServer: true, existedAtRead: false).SaveGuard;

        await Assert.That(guard).IsNotNull();
        await Assert.That(guard!(Naming("https://elsewhere.example"))).IsTrue();
        await Assert.That(guard(Naming(null))).IsTrue();
        await Assert.That(guard(WithoutTheProfile())).IsFalse();
    }

    [Test]
    public async Task A_precondition_also_requires_the_profile_to_still_name_the_server() {
        var guard = Target(
            pointsAtServer: false, adoptServer: true, existedAtRead: false,
            precondition: new CommitPrecondition.ExpectServer(Server)).SaveGuard;

        await Assert.That(guard).IsNotNull();
        await Assert.That(guard!(Naming("https://elsewhere.example"))).IsFalse();
        await Assert.That(guard(Naming(Server))).IsTrue();
        await Assert.That(guard(WithoutTheProfile())).IsFalse();
    }
}
