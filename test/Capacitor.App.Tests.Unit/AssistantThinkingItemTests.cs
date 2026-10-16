using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The collapsed header is the first line that still has text once fences and empty markup are gone.
public class AssistantThinkingItemTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_collapsed_header_skips_a_leading_fence_and_an_empty_mark() {
        await RunOnUiAsync(async () => {
            var fenced = Collapsed("```\nChoosing the lock next.\n```");
            await Assert.That(fenced.HeaderLabel).IsEqualTo("Thinking · Choosing the lock next.");

            var language = Collapsed("```python\nprint(1)\n```");
            await Assert.That(language.HeaderLabel).IsEqualTo("Thinking · print(1)");

            var marks = Collapsed("``\nThe choice.\n");
            await Assert.That(marks.HeaderLabel).IsEqualTo("Thinking · The choice.");

            var none = Collapsed("```\n```");
            await Assert.That(none.HeaderLabel).IsEqualTo("Thinking");
        });
    }

    static AssistantThinkingItem Collapsed(string text) {
        var item = new AssistantThinkingItem(text);
        item.Toggle();
        return item;
    }
}
