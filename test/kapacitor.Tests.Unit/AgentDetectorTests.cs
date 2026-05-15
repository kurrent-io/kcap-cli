using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class AgentDetectorTests {
    [Test]
    public async Task Pure_returns_true_when_path_dir_has_executable_match() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["/usr/local/bin", "/usr/bin"],
            extensions: [""],
            isExecutable: path => path == "/usr/local/bin/claude");

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Pure_returns_false_when_predicate_rejects_all_candidates() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["/usr/local/bin"],
            extensions: [""],
            isExecutable: _ => false);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_returns_false_when_paths_empty() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [],
            extensions: [""],
            isExecutable: _ => true);

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_windows_shaped_detects_cmd_extension() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [@"C:\Users\me\AppData\Roaming\npm"],
            extensions: [".EXE", ".CMD"],
            isExecutable: path => path.EndsWith(".CMD"));

        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Pure_windows_shaped_rejects_bare_name_when_pathext_set() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      [@"C:\some\dir"],
            extensions: [".EXE", ".CMD"],
            isExecutable: path => !path.EndsWith(".EXE") && !path.EndsWith(".CMD"));

        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Pure_skips_empty_path_entry_without_throwing() {
        var found = AgentDetector.IsInstalled(
            binaryName: "claude",
            paths:      ["", "/usr/local/bin"],
            extensions: [""],
            isExecutable: path => path == "/usr/local/bin/claude");

        await Assert.That(found).IsTrue();
    }
}
