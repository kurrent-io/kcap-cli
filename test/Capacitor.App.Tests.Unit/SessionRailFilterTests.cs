using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class SessionRailFilterTests {
    static SessionRailViewModel Build(FakeAgentDirectory directory, bool scopeLocked = false, RailPreferences? preferences = null) =>
        new(directory, _ => { }, _ => { }, TimeProvider.System, preferences: preferences, scopeLocked: scopeLocked);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Filters_start_closed_and_toggle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory);
            var raised = new List<string?>();
            rail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            await Assert.That(rail.FiltersOpen).IsFalse();
            await rail.ToggleFiltersCommand.Execute();
            await Assert.That(rail.FiltersOpen).IsTrue();
            await rail.ToggleFiltersCommand.Execute();
            await Assert.That(rail.FiltersOpen).IsFalse();
            await Assert.That(raised.Count(n => n == nameof(SessionRailViewModel.FiltersOpen))).IsEqualTo(2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dot_shows_when_any_control_differs_from_the_defaults() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory);
            var raised = 0;
            rail.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionRailViewModel.FiltersDiffer)) raised++; };
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Scope = RailScope.Team;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Scope = RailScope.Mine;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.State = RailState.Live;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.State = RailState.All;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Grouping = RailGrouping.Date;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Grouping = RailGrouping.Repository;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Sort = RailSort.Title;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Sort = RailSort.LastUpdated;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Descending = false;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Descending = true;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            await Assert.That(raised).IsEqualTo(10);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Locked_scope_with_defaults_shows_no_dot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory, scopeLocked: true, preferences: RailPreferences.Default with { Scope = RailScope.Team });
            await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
            await Assert.That(rail.FiltersDiffer).IsFalse();
        });
    }
}
