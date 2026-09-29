using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class NotificationSessionSubscriptionsTests {
    [Test]
    public async Task Tracks_current_sessions_without_rejoining_unchanged_entries_and_releases_every_lease() {
        using var sessions = new BehaviorSubject<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> {
            ["s1"] = "a1",
        });
        var acquired = new List<string>();
        var released = new List<string>();
        var subscriptions = new NotificationSessionSubscriptions(sessions, id => {
            acquired.Add(id);
            return Disposable.Create(() => released.Add(id));
        }, ImmediateScheduler.Instance);
        sessions.OnNext(new Dictionary<string, string> { ["s1"] = "a1", ["s2"] = "a2" });
        sessions.OnNext(new Dictionary<string, string> { ["s2"] = "a2" });
        await Assert.That(acquired).IsEquivalentTo(["s1", "s2"]);
        await Assert.That(released).IsEquivalentTo(["s1"]);
        subscriptions.Dispose();
        sessions.OnNext(new Dictionary<string, string> { ["s3"] = "a3" });
        await Assert.That(acquired).IsEquivalentTo(["s1", "s2"]);
        await Assert.That(released).IsEquivalentTo(["s1", "s2"]);
    }
}
