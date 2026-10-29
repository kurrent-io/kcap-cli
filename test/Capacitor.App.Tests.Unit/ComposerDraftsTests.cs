using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;

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

    /// Pins that a draft handed to the other lane is still the one a late delivery clears, and that
    /// the composer watching it there is told.
    [Test]
    [Arguments(false, "")]
    [Arguments(true, "edited")]
    public async Task A_moved_draft_is_cleared_by_its_delivery_where_it_now_lives(bool editedAfterMove, string expected) {
        var drafts = new ComposerDrafts();
        drafts.Set(AgentOrigin.Remote, "a1", "hello");
        var sent = drafts.Revision(AgentOrigin.Remote, "a1");
        var shown = new List<string>();
        using var watch = drafts.Watch(AgentOrigin.Local, "a1", shown.Add);

        drafts.Move(AgentOrigin.Remote, AgentOrigin.Local, "a1");
        if (editedAfterMove) drafts.Set(AgentOrigin.Local, "a1", "edited");
        drafts.ClearDelivered(sent);

        await Assert.That(drafts.Get(AgentOrigin.Remote, "a1")).IsEqualTo("");
        await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(expected);
        await Assert.That(shown).IsEquivalentTo(editedAfterMove ? new[] { "hello" } : new[] { "hello", "" }, CollectionOrdering.Matching);
    }
}
