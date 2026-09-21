using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;
using ReactiveUnit = System.Reactive.Unit;

namespace Capacitor.App.Tests.Unit;

public class DesktopNotificationAccessPromptTests {
    sealed class Harness : IDisposable {
        public FakeDesktopNotificationAccess Access { get; }
        public BehaviorSubject<NotificationPreferences> Preferences { get; }
        public Subject<ReactiveUnit> Activated { get; } = new();
        public bool Foreground { get; set; }
        readonly DesktopNotificationAccessPrompt _prompt;

        // The preference stream replays on subscribe, so the foreground state has to be settled first.
        public Harness(DesktopNotificationAccess current, NotificationPreferences? preferences = null, bool foreground = true) {
            Foreground = foreground;
            Access = new FakeDesktopNotificationAccess(current);
            Preferences = new BehaviorSubject<NotificationPreferences>(preferences ?? new NotificationPreferences());
            _prompt = new DesktopNotificationAccessPrompt(Access, Preferences, Activated, () => Foreground, ImmediateScheduler.Instance);
        }

        public void Activate() => Activated.OnNext(ReactiveUnit.Default);
        public void StopPrompt() => _prompt.Dispose();

        public void Dispose() {
            _prompt.Dispose();
            Preferences.Dispose();
            Activated.Dispose();
        }
    }

    [Test]
    public async Task Undetermined_access_is_requested_once_across_activations() {
        using var app = new Harness(DesktopNotificationAccess.NotDetermined);
        app.Access.AfterRequest = DesktopNotificationAccess.NotDetermined;

        app.Activate();
        app.Activate();

        await Assert.That(app.Access.Requests).IsEqualTo(1);
    }

    [Test]
    [Arguments(DesktopNotificationAccess.Unknown)]
    [Arguments(DesktopNotificationAccess.Denied)]
    [Arguments(DesktopNotificationAccess.Allowed)]
    public async Task Settled_access_is_never_requested(DesktopNotificationAccess current) {
        using var app = new Harness(current);

        app.Activate();

        await Assert.That(app.Access.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task Switching_a_notification_on_asks_without_waiting_for_another_activation() {
        using var app = new Harness(DesktopNotificationAccess.NotDetermined, new NotificationPreferences(false, false, false));
        app.Activate();
        await Assert.That(app.Access.Requests).IsEqualTo(0);

        app.Preferences.OnNext(new NotificationPreferences(false, false, true));

        await Assert.That(app.Access.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task Nothing_is_requested_while_the_app_is_in_the_background() {
        using var app = new Harness(DesktopNotificationAccess.NotDetermined, foreground: false);
        app.Activate();
        await Assert.That(app.Access.Requests).IsEqualTo(0);

        app.Foreground = true;
        app.Activate();

        await Assert.That(app.Access.Requests).IsEqualTo(1);
    }

    /// The read is asynchronous, so the user can leave between the activation and its answer.
    [Test]
    public async Task A_read_that_outlives_the_foreground_does_not_prompt_and_the_next_activation_retries() {
        var read = new TaskCompletionSource<DesktopNotificationAccess>();
        using var app = new Harness(DesktopNotificationAccess.NotDetermined, new NotificationPreferences(false, false, false));
        app.Access.NextRead = read.Task;
        app.Preferences.OnNext(new NotificationPreferences());
        await Assert.That(app.Access.Reads).IsEqualTo(1);

        app.Foreground = false;
        read.SetResult(DesktopNotificationAccess.NotDetermined);
        await Assert.That(app.Access.Requests).IsEqualTo(0);

        app.Foreground = true;
        app.Activate();

        await Assert.That(app.Access.Requests).IsEqualTo(1);
    }

    [Test]
    public async Task A_disposed_prompt_ignores_activations() {
        using var app = new Harness(DesktopNotificationAccess.NotDetermined, foreground: false);
        app.Activate();
        app.StopPrompt();

        app.Foreground = true;
        app.Activate();

        await Assert.That(app.Access.Requests).IsEqualTo(0);
    }
}
