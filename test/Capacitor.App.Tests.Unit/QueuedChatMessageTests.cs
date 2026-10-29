using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class QueuedChatMessageTests {
    [Test]
    public async Task An_unknown_turn_does_not_claim_the_agent_is_idle_or_working() {
        var row = new QueuedChatMessage("hello", 0, 0, 0, []);
        row.SetPickup(null, true, true, false);
        await Assert.That(row.PickupNote).IsEqualTo(QueuedChatMessage.UnknownPickupNote);
        await Assert.That(row.CanSendNow).IsTrue();
    }
    /// A queued prompt that was only attachments reaches the app with empty text, and its row must
    /// still say what it is.
    [Test]
    [Arguments("do it", "do it")]
    [Arguments("", QueuedChatMessage.AttachmentsOnlyText)]
    [Arguments(" \n", QueuedChatMessage.AttachmentsOnlyText)]
    public async Task A_row_without_text_shows_the_attachments_placeholder(string text, string shown) {
        var row = QueuedChatMessage.FromServer(new QueuedInputItem { DispatchId = Guid.NewGuid(), Text = text });

        await Assert.That(row.DisplayText).IsEqualTo(shown);
    }
}
