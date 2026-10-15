using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ComposerDraftsTests {
    /// Pins the key: either form of one agent's GUID finds its draft, and the other lane's agent of
    /// the same id does not.
    [Test]
    [Arguments(AgentOrigin.Local, "0b5f3c2e-8a41-4a8e-9a0e-3f1b2c4d5e6f", "half a thought")]
    [Arguments(AgentOrigin.Local, "0b5f3c2e8a414a8e9a0e3f1b2c4d5e6f", "half a thought")]
    [Arguments(AgentOrigin.Remote, "0b5f3c2e-8a41-4a8e-9a0e-3f1b2c4d5e6f", "")]
    public async Task A_draft_is_found_under_its_own_agent_only(AgentOrigin origin, string agentId, string expected) {
        var drafts = new ComposerDrafts();
        drafts.Set(AgentOrigin.Local, "0B5F3C2E-8A41-4A8E-9A0E-3F1B2C4D5E6F", "half a thought");

        await Assert.That(drafts.Get(origin, agentId)).IsEqualTo(expected);
    }
}
