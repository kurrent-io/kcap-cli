using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class NotificationSettingsServiceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Missing_file_defaults_every_category_on_and_replays_current_state() {
        using var service = new NotificationSettingsService(Tmp.PathTo("notifications.json"));

        var replayed = await service.Changes.Take(1).ToTask();

        await Assert.That(service.Current).IsEqualTo(new NotificationPreferences());
        await Assert.That(replayed).IsEqualTo(new NotificationPreferences());
    }

    [Test]
    public async Task Missing_fields_and_corrupt_data_default_to_enabled() {
        var partial = Tmp.CreateFile("partial.json", """{"permissions":false}""");
        var corrupt = Tmp.CreateFile("corrupt.json", "{not json");

        using var partialService = new NotificationSettingsService(partial);
        using var corruptService = new NotificationSettingsService(corrupt);

        await Assert.That(partialService.Current).IsEqualTo(new NotificationPreferences(false, true, true));
        await Assert.That(corruptService.Current).IsEqualTo(new NotificationPreferences());
    }

    [Test]
    public async Task Save_persists_each_switch_and_a_reopened_service_reads_them() {
        var path = Tmp.PathTo("nested", "notifications.json");
        using var service = new NotificationSettingsService(path);

        var saved = await service.SaveAsync(new NotificationPreferences(false, true, false));

        await Assert.That(saved).IsTrue();
        using var reopened = new NotificationSettingsService(path);
        await Assert.That(reopened.Current).IsEqualTo(new NotificationPreferences(false, true, false));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        await Assert.That(json.RootElement.GetProperty("permissions").GetBoolean()).IsFalse();
        await Assert.That(json.RootElement.GetProperty("questions").GetBoolean()).IsTrue();
        await Assert.That(json.RootElement.GetProperty("idle").GetBoolean()).IsFalse();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp*")).IsEmpty();
    }

    [Test]
    public async Task Failed_persistence_still_applies_and_publishes_the_live_preference() {
        var path = Tmp.CreateDir("notifications.json").Path;
        using var service = new NotificationSettingsService(path);
        var observed = new List<NotificationPreferences>();
        using var subscription = service.Changes.Subscribe(observed.Add);
        var desired = new NotificationPreferences(true, false, true);

        var saved = await service.SaveAsync(desired);

        await Assert.That(saved).IsFalse();
        await Assert.That(service.Current).IsEqualTo(desired);
        await Assert.That(observed).IsEquivalentTo([new NotificationPreferences(), desired]);
    }

    [Test]
    public async Task Overlapping_saves_leave_the_last_preference_live_and_persisted() {
        var path = Tmp.PathTo("notifications.json");
        using var service = new NotificationSettingsService(path);
        var saves = Enumerable.Range(0, 100)
            .Select(i => service.SaveAsync(new NotificationPreferences(i % 2 == 0, i % 3 == 0, i % 5 == 0)))
            .ToList();
        var last = new NotificationPreferences(false, false, false);
        saves.Add(service.SaveAsync(last));

        await Assert.That(service.Current).IsEqualTo(last);
        await Task.WhenAll(saves);

        await Assert.That(saves.All(task => task.Result)).IsTrue();
        using var reopened = new NotificationSettingsService(path);
        await Assert.That(reopened.Current).IsEqualTo(last);
    }

    [Test]
    public async Task Disposing_with_saves_in_flight_drains_them_without_faulting() {
        var path = Tmp.PathTo("notifications.json");
        var service = new NotificationSettingsService(path);
        var saves = Enumerable.Range(0, 100)
            .Select(i => service.SaveAsync(new NotificationPreferences(i % 2 == 0, true, true)))
            .ToList();
        var last = new NotificationPreferences(false, false, false);
        saves.Add(service.SaveAsync(last));

        service.Dispose();
        await Task.WhenAll(saves);

        await Assert.That(saves.All(task => task.Result)).IsTrue();
        await Assert.That(await service.SaveAsync(new NotificationPreferences())).IsFalse();
        using var reopened = new NotificationSettingsService(path);
        await Assert.That(reopened.Current).IsEqualTo(last);
    }
}
