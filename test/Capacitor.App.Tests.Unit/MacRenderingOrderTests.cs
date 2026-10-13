using Avalonia;

namespace Capacitor.App.Tests.Unit;

public class MacRenderingOrderTests {
    static readonly AvaloniaNativeRenderingMode[] Default = [
        AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software,
    ];

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("metal")]
    [Arguments("vulkan")]
    public async Task Unset_metal_or_unknown_keeps_metal_first(string? value) {
        await Assert.That(MacRenderingOrder.Resolve(value)).IsEquivalentTo(Default);
    }

    [Test]
    public async Task Opengl_leaves_metal_out() {
        await Assert.That(MacRenderingOrder.Resolve(" OpenGL "))
            .IsEquivalentTo([AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software]);
    }

    [Test]
    public async Task Software_is_software_only() {
        await Assert.That(MacRenderingOrder.Resolve("software")).IsEquivalentTo([AvaloniaNativeRenderingMode.Software]);
    }
}
