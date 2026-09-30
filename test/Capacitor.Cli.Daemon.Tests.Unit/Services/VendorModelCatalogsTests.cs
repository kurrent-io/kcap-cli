using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class VendorModelCatalogsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static Dictionary<string, VendorModelOption[]> Cat(params (string Vendor, VendorModelOption[] Models)[] entries) =>
        entries.ToDictionary(e => e.Vendor, e => e.Models, StringComparer.Ordinal);

    static readonly VendorModelOption A = new("p/a", "A · p");
    static readonly VendorModelOption B = new("p/b", "B · p");

    [Test]
    public async Task Equal_is_true_for_same_keys_and_ordered_pairs_from_fresh_objects() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [new("p/a", "A · p"), new("p/b", "B · p")])))).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(null, null)).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(Cat(), Cat())).IsTrue();
    }

    [Test]
    public async Task Equal_is_false_for_reorder_relabel_extra_key_or_null_vs_empty() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [B, A])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [new("p/a", "other")])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [A]), ("kiro", [])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("kiro", [A])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(null, Cat())).IsFalse();
    }

    [Test]
    public async Task Merge_takes_fresh_answers_keeps_previous_on_null_reprobe_and_returns_a_new_instance() {
        var previous = Cat(("pi", [A]), ("other", [B]));
        var fresh    = Cat(("pi", [A, B]));            // "other" was probed and answered null

        var merged = VendorModelCatalogs.Merge(previous, fresh);

        await Assert.That(ReferenceEquals(merged, previous)).IsFalse();
        await Assert.That(merged["pi"]).IsEquivalentTo([A, B], CollectionOrdering.Matching);
        await Assert.That(merged["other"]).IsEquivalentTo([B]);
        await Assert.That(previous["pi"]).IsEquivalentTo([A]);
    }

    [Test]
    public async Task Merge_over_no_previous_is_the_fresh_answers() {
        var merged = VendorModelCatalogs.Merge(null, Cat(("pi", [])));
        await Assert.That(merged["pi"]).IsEmpty();
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Probe_folds_only_non_null_answers_keyed_by_vendor() {
        var skipped = new StubCatalogFactory("codex", [B]);
        IHostedAgentRuntimeFactory[] factories = [
            new StubCatalogFactory("pi", [A]),
            new StubCatalogFactory("kiro", []),
            new StubCatalogFactory("claude", null),
            skipped,
        ];

        var result = await VendorModelCatalogs.ProbeAsync(factories, ["pi", "kiro", "claude"], CancellationToken.None);

        await Assert.That(result.Keys).IsEquivalentTo(["pi", "kiro"]);
        await Assert.That(result["kiro"]).IsEmpty();
        await Assert.That(skipped.ProbeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Fingerprint_records_an_empty_array_for_a_factory_with_no_paths() {
        var fp = VendorModelCatalogs.FingerprintCatalogPaths([new StubCatalogFactory("claude", null)], ["claude"]);
        await Assert.That(fp["claude"]).IsEmpty();
    }

    [Test]
    public async Task CatalogPathStat_distinguishes_missing_from_present() {
        var path    = Tmp.PathTo("auth.json");
        var missing = CatalogPathStat.Of(path);
        Tmp.CreateFile("auth.json", "{}");
        var present = CatalogPathStat.Of(path);

        await Assert.That(missing.Exists).IsFalse();
        await Assert.That(present.Exists).IsTrue();
        await Assert.That(missing).IsNotEqualTo(present);
    }
}
