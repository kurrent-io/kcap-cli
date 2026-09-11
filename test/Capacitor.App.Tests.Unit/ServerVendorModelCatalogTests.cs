using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class ServerVendorModelCatalogTests {
    static Dictionary<string, IReadOnlyList<ModelChoice>> Sample() =>
        new(StringComparer.OrdinalIgnoreCase) { ["codex"] = [new("gpt-x", "GPT-X")] };

    [Test]
    public async Task LoadAsync_publishes_a_successful_fetch() {
        var sample = Sample();
        using var catalog = new ServerVendorModelCatalog(
            _ => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?>(sample));
        IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? seen = null;
        using (catalog.Catalog.Subscribe(c => seen = c))
            await catalog.LoadAsync();

        await Assert.That(seen!.ContainsKey("codex")).IsTrue();
        await Assert.That(seen!["codex"].Single().Slug).IsEqualTo("gpt-x");
    }

    [Test]
    public async Task A_null_or_failing_fetch_leaves_the_empty_snapshot() {
        using var nullFetch = new ServerVendorModelCatalog(
            _ => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?>(null));
        using var throwing = new ServerVendorModelCatalog(
            _ => throw new InvalidOperationException("boom"));
        IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? afterNull = null, afterThrow = null;

        using (nullFetch.Catalog.Subscribe(c => afterNull = c))
            await nullFetch.LoadAsync();
        using (throwing.Catalog.Subscribe(c => afterThrow = c))
            await throwing.LoadAsync();

        await Assert.That(afterNull!.Count).IsEqualTo(0);
        await Assert.That(afterThrow!.Count).IsEqualTo(0);
    }
}
