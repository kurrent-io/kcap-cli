using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.Tests.Unit.Install;

/// <summary>A binary is a script install's only when it sits in <c>bin</c> under <c>versions/&lt;v&gt;</c> or
/// <c>current</c> of a root carrying the installer's marker; Stabilize maps the versioned form to the
/// <c>current</c> one so persisted paths follow an update.</summary>
public class ScriptInstallLayoutTests {
    [TempDir] public required TempDir Tmp { get; init; }

    ScriptInstallLayout Seed(bool marker = true) {
        var layout = new ScriptInstallLayout(Tmp.PathTo("kcap"));
        Tmp.CreateFile("kcap/versions/1.2.0/bin/kcap-daemon");
        Tmp.CreateFile("kcap/versions/1.3.0/bin/kcap-daemon");
        if (marker) Tmp.CreateFile("kcap/install.json", """{"source":"script","channel":"latest"}""");
        // A directory standing in for the link: only what resolves through it matters here.
        Tmp.CreateFile("kcap/current/bin/kcap-daemon");
        return layout;
    }

    [Test]
    public async Task A_versioned_binary_belongs_to_the_install() {
        var layout = Seed();

        var found = ScriptInstallLayout.FromBinary(Path.Combine(layout.VersionDir("1.2.0"), "bin", "kcap"), File.Exists);

        await Assert.That(found).IsEqualTo(layout);
    }

    [Test]
    public async Task A_binary_reached_through_current_belongs_to_the_install() {
        var layout = Seed();

        await Assert.That(ScriptInstallLayout.FromBinary(layout.CurrentBin("kcap"), File.Exists)).IsEqualTo(layout);
    }

    [Test]
    public async Task Without_the_marker_the_same_shape_is_not_a_script_install() {
        var layout = Seed(marker: false);

        await Assert.That(ScriptInstallLayout.FromBinary(layout.CurrentBin("kcap"), File.Exists)).IsNull();
    }

    [Test]
    [Arguments("/opt/npm/node_modules/@kurrent/kcap-linux-x64/bin/kcap")]
    [Arguments("/usr/local/bin/kcap")]
    [Arguments("kcap")]
    [Arguments("")]
    public async Task Other_shapes_are_not_script_installs(string path) {
        await Assert.That(ScriptInstallLayout.FromBinary(path, _ => true)).IsNull();
    }

    [Test]
    public async Task Stabilize_maps_any_version_directory_to_current() {
        var layout = Seed();
        var pinned = Path.Combine(layout.VersionDir("1.2.0"), "bin", "kcap-daemon");

        await Assert.That(ScriptInstallLayout.Stabilize(pinned)).IsEqualTo(layout.CurrentBin("kcap-daemon"));
        await Assert.That(ScriptInstallLayout.Stabilize(layout.CurrentBin("kcap-daemon"))).IsEqualTo(layout.CurrentBin("kcap-daemon"));
    }

    /// <summary>A file the active version does not ship keeps its own path rather than a dead link.</summary>
    [Test]
    public async Task Stabilize_keeps_a_path_current_does_not_have() {
        var layout = Seed();
        var only   = Tmp.CreateFile("kcap/versions/1.2.0/bin/legacy-tool");

        await Assert.That(ScriptInstallLayout.Stabilize(only)).IsEqualTo(only);
        await Assert.That(File.Exists(layout.CurrentBin("legacy-tool"))).IsFalse();
    }

    [Test]
    public async Task Stabilize_leaves_other_paths_alone() {
        const string npm = "/opt/npm/node_modules/@kurrent/kcap-linux-x64/bin/kcap-daemon";

        await Assert.That(ScriptInstallLayout.Stabilize(npm, _ => true)).IsEqualTo(npm);
    }
}
