// test/Capacitor.Cli.Tests.Unit/Acp/AcpVendorDescriptorTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Test plan item 7 (Round 2 Finding 2 drops the throwing-construction cases from an
/// earlier revision of this design): pins <see cref="AcpVendorDescriptors.Cursor"/>'s literal
/// field values against today's hard-coded constants — a lightweight guard against an accidental
/// edit to the shared descriptor silently changing Cursor's behavior. There is no
/// <c>SupportsModelSelection</c> flag/invariant left to test — <see cref="AcpVendorDescriptor"/>
/// accepts any <see cref="IAcpModelSelector"/> for <see cref="AcpVendorDescriptor.ModelSelector"/>
/// unconditionally, so the one thing worth asserting is that Cursor's real descriptor constructs
/// successfully and round-trips through equality as expected.
/// </summary>
public class AcpVendorDescriptorTests {
    [Test]
    public async Task Cursor_MatchesTodaysHardCodedConstants() {
        var descriptor = AcpVendorDescriptors.Cursor;

        await Assert.That(descriptor.Vendor).IsEqualTo("cursor");
        await Assert.That(descriptor.Argv.SequenceEqual(["acp"])).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgv.SequenceEqual([])).IsTrue();
        await Assert.That(descriptor.SupportsUnattended).IsFalse();
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
    }

    [Test]
    public async Task Cursor_ResolveBinaryPath_ReadsConfigCursorPath() {
        var config = new DaemonConfig { CursorPath = "/opt/cursor/cursor-agent" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveBinaryPath(config)).IsEqualTo("/opt/cursor/cursor-agent");
    }

    [Test]
    public async Task Cursor_ResolveDefaultModel_ReadsConfigCursorModel() {
        var config = new DaemonConfig { CursorModel = "claude-opus-4-8" };

        await Assert.That(AcpVendorDescriptors.Cursor.ResolveDefaultModel(config)).IsEqualTo("claude-opus-4-8");
    }

    /// <summary>Any <see cref="IAcpModelSelector"/> — including a NoOp one, even though the real
    /// Cursor descriptor never uses it — constructs a valid descriptor. There is no invariant left
    /// to reject this combination (Round 2 Finding 2).</summary>
    [Test]
    public async Task Descriptor_ConstructsSuccessfully_WithAnyModelSelector() {
        var descriptor = new AcpVendorDescriptor(
            Vendor:              "test-vendor",
            ResolveBinaryPath:   _ => "test-vendor-cli",
            ResolveDefaultModel: _ => null,
            Argv:                ["acp"],
            UnattendedTrustArgv: [],
            SupportsUnattended:  false,
            ModelSelector:       NoOpModelSelector.Instance,
            SupportsMcpServers:  false
        );

        await Assert.That(descriptor.ModelSelector).IsEqualTo(NoOpModelSelector.Instance);
    }
}
