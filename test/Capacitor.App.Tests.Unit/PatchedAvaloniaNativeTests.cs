using System.Reflection;
using Avalonia;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class PatchedAvaloniaNativeTests {
    /// The build copies the patched library beside every executable that references the app,
    /// this test host included, where the package's own copy lands too; the two must differ, or
    /// the app would be loading the leak it means to avoid.
    [Test]
    public async Task The_patched_library_ships_beside_the_stock_one_and_differs_from_it() {
        if (!OperatingSystem.IsMacOS()) return;
        var patched = PatchedAvaloniaNative.Path!;
        await Assert.That(File.Exists(patched)).IsTrue();
        var stock = new[] { Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native"), AppContext.BaseDirectory }
            .Select(dir => Path.Combine(dir, "libAvaloniaNative.dylib")).First(File.Exists);
        var patchedBytes = await File.ReadAllBytesAsync(patched);
        var stockBytes = await File.ReadAllBytesAsync(stock);
        var universalMachO = patchedBytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
        var differs = !patchedBytes.AsSpan().SequenceEqual(stockBytes);
        await Assert.That(universalMachO).IsTrue();
        await Assert.That(differs).IsTrue();
    }

    /// The binary's vtables match one Avalonia.Native; a package bump without a rebuild would
    /// otherwise pass every other check, since stale patched bytes still differ from the new stock.
    [Test]
    public async Task The_library_was_built_from_the_Avalonia_version_the_app_loads() {
        if (!OperatingSystem.IsMacOS()) return;
        var stamp = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "libAvaloniaNative.patched.version"))).Trim();
        var informational = typeof(AvaloniaNativePlatformOptions).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        var loaded = informational.Split('+')[0];
        await Assert.That(stamp).IsEqualTo(loaded);
    }

    [Test]
    public async Task No_library_is_named_where_Avalonia_Native_is_not_the_platform() {
        if (OperatingSystem.IsMacOS()) return;
        await Assert.That(PatchedAvaloniaNative.Path).IsNull();
    }
}
