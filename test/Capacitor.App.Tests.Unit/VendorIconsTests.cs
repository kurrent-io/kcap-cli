using Capacitor.App.Views;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Tests.Unit;

/// Every harness draws a brand mark that Avalonia's path parser accepts, not the monogram fallback.
[NotInParallel("AvaloniaSession")]
public class VendorIconsTests {
    public static IEnumerable<string> Vendors() => [.. HarnessRegistry.Identities.Select(h => h.VendorId), "muse-assistant"];

    [Test]
    [MethodDataSource(nameof(Vendors))]
    public async Task Every_harness_has_a_brand_mark(string vendor) {
        var width = await AvaloniaSession.DispatchAsync(() => VendorIcons.For(vendor)?.Bounds.Width ?? 0);

        await Assert.That(width).IsGreaterThan(0);
    }
}
