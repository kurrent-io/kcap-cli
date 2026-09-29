using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class DaemonInfoJsonTests {
    [Test]
    public async Task Vendor_models_deserialize_from_the_registry_shape() {
        var json = """{"name":"d","vendor_models":{"pi":[{"value":"anthropic/claude-opus-5","label":"Claude Opus 5 · anthropic"}]}}""";
        var info = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(info.VendorModels!["pi"][0].Label).IsEqualTo("Claude Opus 5 · anthropic");
    }

    [Test]
    public async Task Missing_vendor_models_is_null() {
        var info = JsonSerializer.Deserialize("""{"name":"d"}""", RemoteModelsJsonContext.Default.DaemonInfo)!;
        await Assert.That(info.VendorModels).IsNull();
    }
}
