using Capacitor.App.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class UiScaleTests {
    [Test]
    [Arguments(null, 1.0)]
    [Arguments(double.NaN, 1.0)]
    [Arguments(double.PositiveInfinity, 1.0)]
    [Arguments(0.1, 0.8)]
    [Arguments(9.0, 1.5)]
    [Arguments(1.13, 1.1)]
    [Arguments(1.16, 1.2)]
    [Arguments(0.95, 1.0)]
    [Arguments(1.05, 1.1)]
    public async Task Normalize_falls_back_clamps_and_snaps_to_a_step(double? stored, double expected) =>
        await Assert.That(UiScale.Normalize(stored)).IsEqualTo(expected);

    [Test]
    [Arguments(1.0, 1.1, 0.9)]
    [Arguments(1.5, 1.5, 1.4)]
    [Arguments(0.8, 0.9, 0.8)]
    public async Task Larger_and_smaller_move_one_step_within_the_range(double from, double larger, double smaller) {
        await Assert.That(UiScale.Larger(from)).IsEqualTo(larger);
        await Assert.That(UiScale.Smaller(from)).IsEqualTo(smaller);
    }

    [Test]
    [Arguments(1.0, null, 1200.0)]
    [Arguments(0.8, null, 1200.0)]
    [Arguments(1.5, null, 1800.0)]
    [Arguments(1.5, 1470.0, 1470.0)]
    [Arguments(1.5, 900.0, 1200.0)]
    public async Task Minimum_grows_with_the_scale_up_to_the_screen_and_never_below_the_unscaled_one(
            double scale, double? available, double expected) =>
        await Assert.That(UiScale.Minimum(1200, scale, available)).IsEqualTo(expected);

    [Test]
    [Arguments(1.0, "100%")]
    [Arguments(0.9, "90%")]
    [Arguments(1.3, "130%")]
    public async Task Label_is_a_whole_percentage(double scale, string label) =>
        await Assert.That(UiScale.Label(scale)).IsEqualTo(label);

    [Test]
    public async Task A_change_is_published_and_kept_for_the_next_start() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var setting = await UiScaleSetting.LoadAsync(new AppStateStore(path));
        var seen = new List<double>();
        using var subscription = setting.Changes.Subscribe(seen.Add);

        await setting.SetAsync(1.2);
        await setting.SetAsync(1.2);

        await Assert.That(seen).IsEquivalentTo([1.0, 1.2], CollectionOrdering.Matching);
        await Assert.That(setting.Current).IsEqualTo(1.2);
        var reloaded = await UiScaleSetting.LoadAsync(new AppStateStore(path));
        await Assert.That(reloaded.Current).IsEqualTo(1.2);
    }

    [Test]
    public async Task A_stored_scale_out_of_range_loads_clamped() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        await store.UpdateAsync(s => s with { UiScale = 4 });

        await Assert.That((await UiScaleSetting.LoadAsync(store)).Current).IsEqualTo(1.5);
    }
}
