using Capacitor.App.Platform;
using Capacitor.App.Platform.Linux;

namespace Capacitor.App.Tests.Unit;

public class LinuxDesktopHostTests {
    [Test]
    public async Task A_new_copy_starts_from_the_image_and_waits_for_this_one_to_exit() {
        using var host = new LinuxDesktopHost("/cli", false, new AppImageInstall("/apps/Kcap.AppImage"), null);

        var start = host.NewCopy("--setup");

        await Assert.That(start.FileName).IsEqualTo("/apps/Kcap.AppImage");
        await Assert.That(start.ArgumentList).Contains("--setup");
        await Assert.That(start.ArgumentList).Contains(DesktopHost.RelaunchArg);
    }
}
