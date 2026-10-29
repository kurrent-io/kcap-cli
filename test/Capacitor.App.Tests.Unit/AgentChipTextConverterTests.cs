using System.Globalization;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class AgentChipTextConverterTests {
    [Test]
    public async Task The_label_is_vendor_and_model_and_gains_the_account_when_one_is_chosen() {
        IReadOnlyList<HarnessOption> options = [new("claude", "Claude", "pty", true)];
        var catalog = new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) {
            ["claude"] = [new("opus", "Opus")],
        };

        string? Label(string? account) => AgentChipTextConverter.Instance.Convert(
            [options, "claude", "opus", catalog, account], typeof(string), null, CultureInfo.InvariantCulture) as string;

        await Assert.That(Label(null)).IsEqualTo("Claude · Opus");
        await Assert.That(Label("work")).IsEqualTo("Claude · Opus · work");
    }
}
