using Capacitor.App.Platform;
using Capacitor.App.Platform.Linux;

namespace Capacitor.App.Tests.Unit;

public class LinuxDesktopHostTests {
    [Test]
    public async Task A_new_copy_starts_from_the_image_and_waits_for_this_one_to_exit() {
        using var host = new LinuxDesktopHost("/cli", false, new AppImageInstall("/apps/Kcap.AppImage"), false, SingleInstanceLock.Unenforced);

        var start = host.NewCopy("--setup");

        await Assert.That(start.FileName).IsEqualTo("/apps/Kcap.AppImage");
        await Assert.That(start.ArgumentList).Contains("--setup");
        await Assert.That(start.ArgumentList).Contains(DesktopHost.RelaunchArg);
    }

    /// A CLI left in the mount is gone once the app exits, so the PATH link is never offered for it
    [Test]
    public async Task A_cli_left_in_the_mount_is_never_linked() {
        using var mounted = new LinuxDesktopHost("/tmp/.mount_kcap/usr/bin", false, new AppImageInstall("/apps/Kcap.AppImage"), true, SingleInstanceLock.Unenforced);
        using var staged  = new LinuxDesktopHost("/data/kcap/desktop/bin", false, new AppImageInstall("/apps/Kcap.AppImage"), false, SingleInstanceLock.Unenforced);

        await Assert.That(App.ShimTargetFor("/tmp/.mount_kcap/usr/bin/kcap", mounted.CliOutlivesApp)).IsNull();
        await Assert.That(App.ShimTargetFor("/data/kcap/desktop/bin/kcap", staged.CliOutlivesApp)).IsEqualTo("/data/kcap/desktop/bin/kcap");
    }
}
