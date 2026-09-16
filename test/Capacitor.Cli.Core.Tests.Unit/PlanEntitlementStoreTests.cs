namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Round-trip, normalization and fail-open coverage for <see cref="PlanEntitlementStore"/>, the
/// durable per-server cache of the <c>X-Kcap-Plan</c> header. Each test writes into its own root, so
/// the hashed per-URL files are private to it.
/// </summary>
public class PlanEntitlementStoreTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string Host(string tag) => $"{tag}.example.com";
    static string Url(string tag)  => $"https://{Host(tag)}";

    [Test]
    public async Task SetThenGet_RoundTrips() {
        var url = Url("roundtrip");
        PlanEntitlementStore.Set(url, "work_items=0,projects=1", Config.Root);

        var plan = PlanEntitlementStore.Get(url, Config.Root);
        await Assert.That(plan.Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(plan.Allows(PlanFeature.Projects)).IsTrue();
    }

    [Test]
    public async Task Get_UnknownServer_AllowsEverything() {
        await Assert.That(PlanEntitlementStore.Get(Url("unknown"), Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Get_BlankUrl_AllowsEverything() {
        await Assert.That(PlanEntitlementStore.Get(null, Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
        await Assert.That(PlanEntitlementStore.Get("   ", Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Set_NormalizesTrailingSlashAndCase() {
        var host = Host("normalize");
        PlanEntitlementStore.Set($"https://{host}/", "work_items=0", Config.Root);

        // A different-cased, slash-free spelling of the same server resolves the same entry.
        await Assert.That(PlanEntitlementStore.Get($"HTTPS://{host.ToUpperInvariant()}", Config.Root)
            .Allows(PlanFeature.WorkItems)).IsFalse();
    }

    [Test]
    public async Task Set_KeepsServersApart() {
        PlanEntitlementStore.Set(Url("denied"), "work_items=0", Config.Root);
        PlanEntitlementStore.Set(Url("allowed"), "work_items=1", Config.Root);

        await Assert.That(PlanEntitlementStore.Get(Url("denied"),  Config.Root).Allows(PlanFeature.WorkItems)).IsFalse();
        await Assert.That(PlanEntitlementStore.Get(Url("allowed"), Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Set_AnAllowingHeader_ClearsAnEarlierDenial() {
        // The upgrade path: Free → Team is observed as a header that denies nothing.
        var url = Url("upgrade");
        PlanEntitlementStore.Set(url, "work_items=0", Config.Root);
        PlanEntitlementStore.Set(url, "work_items=1", Config.Root);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Get_AStaleAnswer_AllowsEverything() {
        // Only reachable when the server stopped sending the header; a denial must not outlive it
        // forever, or a paying tenant loses the nudge with no way back.
        var url = Url("stale");
        var old = DateTimeOffset.UtcNow - PlanEntitlementStore.StaleAfter - TimeSpan.FromHours(1);
        PlanEntitlementStore.Set(url, "work_items=0", Config.Root, now: old);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }

    [Test]
    public async Task Get_AnAnswerInsideTheHorizon_StillDenies() {
        var url    = Url("fresh");
        var recent = DateTimeOffset.UtcNow - PlanEntitlementStore.StaleAfter + TimeSpan.FromHours(1);
        PlanEntitlementStore.Set(url, "work_items=0", Config.Root, now: recent);

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root).Allows(PlanFeature.WorkItems)).IsFalse();
    }

    [Test]
    public async Task Get_ACorruptFile_AllowsEverything() {
        var url = Url("corrupt");
        PlanEntitlementStore.Set(url, "work_items=0", Config.Root);

        foreach (var file in Directory.EnumerateFiles(Config.Directory, "plan-entitlements-*.json"))
            File.WriteAllText(file, "{ not json");

        await Assert.That(PlanEntitlementStore.Get(url, Config.Root).Allows(PlanFeature.WorkItems)).IsTrue();
    }
}
