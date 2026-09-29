using Capacitor.Cli.Commands.Capture;

namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

public class CaptureRepairArgsTests {
    [Test]
    [Arguments("--all")]
    [Arguments("--org")]
    [Arguments("--repo")]
    [Arguments("--cwd")]
    [Arguments("--since")]
    [Arguments("--min-lines")]
    [Arguments("--reimport")]
    [Arguments("--discover")]
    [Arguments("--generate-summaries")]
    [Arguments("--private")]
    [Arguments("--cursor")]
    [Arguments("--misspelled")]
    public async Task Rejects_normal_import_selection_or_actions(string flag) {
        var error = CaptureRepairArgs.Validate(["import", "--session", "abc123", "--repair-capture", flag]);
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Requires_exactly_one_explicit_session() {
        await Assert.That(CaptureRepairArgs.Validate(["import", "--repair-capture"])).IsNotNull();
        await Assert.That(CaptureRepairArgs.Validate(["import", "--repair-capture", "--session", "--dry-run"])).IsNotNull();
        await Assert.That(CaptureRepairArgs.Validate(["import", "--repair-capture", "--session", "one", "--session", "two"])).IsNotNull();
    }

    [Test]
    public async Task Accepts_targeted_preview_with_vendor_and_server() {
        await Assert.That(CaptureRepairArgs.Validate(["import", "--session", "abc123", "--repair-capture", "--dry-run", "--claude", "--server-url", "https://example.test"])).IsNull();
    }
}
