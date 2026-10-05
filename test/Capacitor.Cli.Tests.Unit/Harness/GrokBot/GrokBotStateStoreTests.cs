using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins that delivery state survives a round trip, and that a missing or corrupt file reads as
/// "nothing delivered" rather than failing the watcher.</summary>
public class GrokBotStateStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task State_round_trips_with_and_without_an_open_session() {
        var store = new GrokBotStateStore(Tmp.PathTo("grok-bot", "state.json"));
        var saved = new Dictionary<string, GrokBotBotState> {
            ["a"] = new(42, new GrokBotOpenSession("0123456789abcdef0123456789abcdef", "t3u", 1_790_000_000_000, 1_790_000_100_000)),
            ["b"] = new(7, null)
        };

        store.Save(saved);
        var loaded = store.Load();

        await Assert.That(loaded["a"]).IsEqualTo(saved["a"]);
        await Assert.That(loaded["b"]).IsEqualTo(saved["b"]);
    }

    [Test]
    public async Task Missing_or_corrupt_state_loads_empty() {
        await Assert.That(new GrokBotStateStore(Tmp.PathTo("absent.json")).Load()).IsEmpty();

        var corrupt = Tmp.CreateFile("corrupt.json", "{not json");
        await Assert.That(new GrokBotStateStore(corrupt).Load()).IsEmpty();
    }
}
