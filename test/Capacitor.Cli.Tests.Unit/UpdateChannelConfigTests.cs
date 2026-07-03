using System.Text.Json;
using Capacitor.Cli.Core.Config;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateChannelConfigTests {
    // NOTE: Deliberately constructs CapacitorConfig directly rather than
    // JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.CapacitorConfig).
    // The source-generated ConfigJsonContext deserializes CapacitorConfig via a
    // parameterized-constructor path that does NOT apply C# member-initializer
    // defaults for properties absent from the payload — it substitutes
    // default(T) (null for string) instead. This is a pre-existing behavior of
    // this JsonSerializerContext (confirmed independent of this change; it
    // already affects sibling properties UpdateCheck and DefaultVisibility the
    // same way, which is why AppConfig.Load() normalizes DefaultVisibility with
    // `?? "org_public"`). Deserializing "{}" here would make this test always
    // fail regardless of the property's default, so it is out of scope for this
    // assertion. See task-3-report.md for the full analysis and the resulting
    // concern for callers of AppConfig.Load().
    [Test]
    public async Task Defaults_to_latest_when_absent() {
        var cfg = new CapacitorConfig();
        await Assert.That(cfg.UpdateChannel).IsEqualTo("latest");
    }

    [Test]
    public async Task Round_trips_beta() {
        var json = JsonSerializer.Serialize(
            new CapacitorConfig { UpdateChannel = "beta" },
            ConfigJsonContext.Default.CapacitorConfig);
        await Assert.That(json).Contains("update_channel");
        await Assert.That(json).Contains("beta");
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.CapacitorConfig)!;
        await Assert.That(back.UpdateChannel).IsEqualTo("beta");
    }
}
