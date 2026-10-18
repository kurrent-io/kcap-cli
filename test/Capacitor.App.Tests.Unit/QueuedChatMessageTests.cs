using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class QueuedChatMessageTests {
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
