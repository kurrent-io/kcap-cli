using kapacitor.Commands;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ConfigCommandTests {
    [Test]
    public async Task ApplySet_DisableSessionGuidelines_True_UpdatesProfile() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "disable_session_guidelines", "true");

        await Assert.That(updated.DisableSessionGuidelines).IsTrue();
    }

    [Test]
    public async Task ApplySet_DisableSessionGuidelines_False_UpdatesProfile() {
        var profile = new Profile { DisableSessionGuidelines = true };

        var updated = ConfigCommand.ApplySet(profile, "disable_session_guidelines", "false");

        await Assert.That(updated.DisableSessionGuidelines).IsFalse();
    }

    [Test]
    public async Task ApplySet_DisableSessionGuidelines_InvalidValue_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "disable_session_guidelines", "maybe"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_UnknownKey_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "made_up_key", "x"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_UpdateCheck_PreservesExistingBehavior() {
        var profile = new Profile { UpdateCheck = true };

        var updated = ConfigCommand.ApplySet(profile, "update_check", "false");

        await Assert.That(updated.UpdateCheck).IsFalse();
    }

    [Test]
    public async Task ApplySet_ServerUrl_StoresValueVerbatim() {
        // ApplySet itself stays pure — normalization happens in Set, not here.
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "server_url", "https://example.com");

        await Assert.That(updated.ServerUrl).IsEqualTo("https://example.com");
    }
}
