using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit;

public class KcapBundleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Bundle(string name, string id, string? executableKey, string executableFile, string version = "1.0.0") {
        var root = Tmp.CreateDir(name);
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        var executable = executableKey is null ? "" : $"<key>CFBundleExecutable</key><string>{executableKey}</string>";
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleIdentifier</key><string>{id}</string>{executable}<key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", executableFile), "exe");
        return root;
    }

    [Test]
    public async Task The_current_identity_is_verified_by_its_desktop_executable() {
        var bundle = KcapBundle.Read(Bundle("kcap.app", "io.kurrent.kcap", null, "kcap-desktop", "1.4.0"));

        await Assert.That(bundle is { Legacy: false, Version: "1.4.0", Executable: "kcap-desktop" }).IsTrue();
    }

    [Test]
    [Arguments("Kurrent Capacitor.app", "Kurrent Capacitor")]
    [Arguments("kcap.app", "kcap-desktop")]
    public async Task Both_pre_rebrand_layouts_are_recognised(string name, string executable) {
        var bundle = KcapBundle.Read(Bundle(name, "io.kurrent.capacitor", executable, executable, "1.3.1"));

        await Assert.That(bundle is { Legacy: true, Version: "1.3.1" }).IsTrue();
        await Assert.That(bundle!.Executable).IsEqualTo(executable);
    }

    [Test]
    [Arguments("com.example.other", "kcap-desktop", "kcap-desktop")]
    [Arguments("io.kurrent.capacitor", "Other", "Other")]
    [Arguments("io.kurrent.capacitor", "Kurrent Capacitor", "kcap-desktop")]
    public async Task Anything_else_is_not_kcap(string id, string executableKey, string executableFile) {
        await Assert.That(KcapBundle.Read(Bundle("x.app", id, executableKey, executableFile))).IsNull();
    }

    [Test]
    public async Task A_linked_bundle_is_not_verified() {
        var real = Bundle("real.app", "io.kurrent.kcap", null, "kcap-desktop");
        var link = Tmp.PathTo("link.app");
        Directory.CreateSymbolicLink(link, real);

        await Assert.That(KcapBundle.Read(link)).IsNull();
    }
}
