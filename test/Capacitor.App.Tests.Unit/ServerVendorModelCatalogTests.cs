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

    /// A slow earlier load (older generation) that completes AFTER a newer one must not overwrite the
    /// newer snapshot — the generation guard drops the stale result.
    [Test]
    public async Task A_stale_load_does_not_overwrite_a_newer_one() {
        IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> older =
            new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) { ["old"] = [new("o", "O")] };
        IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> newer =
            new Dictionary<string, IReadOnlyList<ModelChoice>>(StringComparer.OrdinalIgnoreCase) { ["new"] = [new("n", "N")] };

        var calls = 0;
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var catalog = new ServerVendorModelCatalog(async _ => {
            if (Interlocked.Increment(ref calls) == 1) {
                firstStarted.SetResult();
                await releaseFirst.Task;   // the older load finishes only after we say so
                return older;
            }
            return newer;                  // the newer load completes immediately
        });
        using var _ = catalog;

        var emissions = new List<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>();
        using var sub = catalog.Catalog.Subscribe(emissions.Add);

        var stale = catalog.LoadAsync();   // generation 1, blocks
        await firstStarted.Task;
        await catalog.LoadAsync();         // generation 2, publishes `newer`
        releaseFirst.SetResult();          // generation 1 now completes with `older` — must be dropped
        await stale;

        await Assert.That(emissions[^1].ContainsKey("new")).IsTrue();
        await Assert.That(emissions.Any(e => e.ContainsKey("old"))).IsFalse();
    }
}
