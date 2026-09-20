using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class MacNotificationCategoriesTests {
    [Test]
    public async Task Closing_the_last_user_unregisters_and_releases_its_category() {
        var registered = new List<nint>();
        var released = new List<nint>();
        using var categories = new MacNotificationCategories((_, _) => 1, values => { registered.Clear(); registered.AddRange(values); }, released.Add);
        var key = categories.Acquire([new("allow", "Allow")]);
        categories.Release(key);
        await Assert.That(registered.Count).IsEqualTo(0);
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1]);
    }

    [Test]
    public async Task Shared_categories_survive_until_the_last_notification_closes() {
        var registered = new List<nint>();
        var released = new List<nint>();
        var created = 0;
        using var categories = new MacNotificationCategories((_, _) => ++created, values => { registered.Clear(); registered.AddRange(values); }, released.Add);
        var first = categories.Acquire([new("allow", "Allow")]);
        var shared = categories.Acquire([new("allow", "Allow")]);
        var other = categories.Acquire([new("open", "Respond in app")]);
        categories.Release(first);
        await Assert.That(registered.ToArray()).IsEquivalentTo([(nint)1, (nint)2]);
        await Assert.That(released.Count).IsEqualTo(0);
        categories.Release(shared);
        await Assert.That(registered.ToArray()).IsEquivalentTo([(nint)2]);
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1]);
        categories.Release(other);
        await Assert.That(registered.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Changing_agent_labels_does_not_accumulate_native_categories() {
        var registered = new List<nint>();
        var released = new List<nint>();
        var created = 0;
        using var categories = new MacNotificationCategories((_, _) => ++created, values => { registered.Clear(); registered.AddRange(values); }, released.Add);
        for (var i = 0; i < 100; i++) {
            var key = categories.Acquire([new("always", $"Always for scope {i}")]);
            await Assert.That(registered.Count).IsEqualTo(1);
            categories.Release(key);
        }
        await Assert.That(released.Count).IsEqualTo(100);
        await Assert.That(registered.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Disposal_unregisters_and_releases_shared_handles_exactly_once() {
        var registered = new List<nint>();
        var released = new List<nint>();
        var created = 0;
        var categories = new MacNotificationCategories((_, _) => ++created, values => { registered.Clear(); registered.AddRange(values); }, released.Add);
        categories.Acquire([new("allow", "Allow")]);
        categories.Acquire([new("allow", "Allow")]);
        categories.Acquire([new("decline", "Decline")]);
        categories.Dispose();
        categories.Dispose();
        await Assert.That(registered.Count).IsEqualTo(0);
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1, (nint)2]);
    }

    [Test]
    public async Task Failed_registration_releases_the_new_native_category() {
        var released = new List<nint>();
        using var categories = new MacNotificationCategories((_, _) => 1, _ => throw new InvalidOperationException("registration failed"), released.Add);
        try { categories.Acquire([new("allow", "Allow")]); } catch (InvalidOperationException) { }
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1]);
    }

    [Test]
    public async Task Failed_reregistration_still_releases_closed_categories() {
        var released = new List<nint>();
        var fail = false;
        using var categories = new MacNotificationCategories((_, _) => 1, _ => { if (fail) throw new InvalidOperationException("registration failed"); }, released.Add);
        var key = categories.Acquire([new("allow", "Allow")]);
        fail = true;
        try { categories.Release(key); } catch (InvalidOperationException) { }
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1]);
    }

    [Test]
    public async Task Failed_disposal_registration_still_releases_all_native_categories() {
        var released = new List<nint>();
        var fail = false;
        var created = 0;
        var categories = new MacNotificationCategories((_, _) => ++created, _ => { if (fail) throw new InvalidOperationException("registration failed"); }, released.Add);
        categories.Acquire([new("allow", "Allow")]);
        categories.Acquire([new("decline", "Decline")]);
        fail = true;
        try { categories.Dispose(); } catch (InvalidOperationException) { }
        await Assert.That(released.ToArray()).IsEquivalentTo([(nint)1, (nint)2]);
    }
}
