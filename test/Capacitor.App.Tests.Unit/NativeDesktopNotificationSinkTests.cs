using Avalonia.Labs.Notifications;
using Avalonia.Media.Imaging;
using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Tests.Unit;

public class NativeDesktopNotificationSinkTests {
    static DesktopNotification Permission(string id = "request") => new(id, "Permission", "cat a && echo <done>", [new("allow", "Allow"), new("decline", "Decline")]);

    [Test]
    public async Task Windows_payload_escapes_command_markup_and_offers_only_supplied_choices() {
        var manager = new Manager();
        using var sink = new NativeDesktopNotificationSink(manager, a => a(), windows: true);
        sink.Show(Permission(), _ => { });
        var shown = manager.Created.Single();
        await Assert.That(shown.Message).IsEqualTo("cat a &amp;&amp; echo &lt;done&gt;");
        await Assert.That(shown.Actions.Select(a => a.Caption).ToArray()).IsEquivalentTo(["Allow", "Decline"]);
        await Assert.That(shown.Shown).IsTrue();
    }

    [Test]
    public async Task Linux_button_activation_keeps_its_action_even_when_IsActivated_is_true() {
        var manager = new Manager();
        using var sink = new NativeDesktopNotificationSink(manager, a => a(), windows: false);
        var replies = new List<string?>();
        sink.Show(Permission(), replies.Add);
        var shown = manager.Created.Single();
        manager.Complete(new() { NotificationId = shown.Id, ActionTag = shown.Actions.Single(a => a.Caption == "Allow").Tag, IsActivated = true });
        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]).IsEqualTo("allow");
    }

    [Test]
    public async Task Closing_before_queued_activation_discards_it() {
        var manager = new Manager();
        var queued = new Queue<Action>();
        using var sink = new NativeDesktopNotificationSink(manager, queued.Enqueue, windows: false);
        var replies = new List<string?>();
        sink.Show(Permission(), replies.Add);
        var shown = manager.Created.Single();
        manager.Complete(new() { NotificationId = shown.Id, ActionTag = shown.Actions.Single(a => a.Caption == "Allow").Tag });
        sink.Close("request");
        while (queued.TryDequeue(out var action)) action();
        await Assert.That(replies.Count).IsEqualTo(0);
        await Assert.That(manager.Created.Single().Closed).IsTrue();
    }

    [Test]
    public async Task Replacing_and_disposing_withdraws_old_toasts_and_rejects_stale_callbacks() {
        var manager = new Manager();
        var replies = new List<string?>();
        var sink = new NativeDesktopNotificationSink(manager, a => a(), windows: false);
        sink.Show(Permission(), replies.Add);
        sink.Show(Permission(), replies.Add);
        manager.Complete(new() { NotificationId = manager.Created[0].Id, ActionTag = manager.Created[0].Actions.Single(a => a.Caption == "Allow").Tag });
        sink.Dispose();
        manager.Complete(new() { NotificationId = manager.Created[1].Id, IsActivated = true });
        await Assert.That(manager.Created.All(n => n.Closed)).IsTrue();
        await Assert.That(replies.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_choices_and_dismissal_never_answer_a_request_but_body_click_opens_it() {
        var manager = new Manager();
        using var sink = new NativeDesktopNotificationSink(manager, a => a(), windows: false);
        var replies = new List<string?>();
        sink.Show(Permission(), replies.Add);
        var id = manager.Created.Single().Id;
        manager.Complete(new() { NotificationId = id, ActionTag = "always", IsActivated = true });
        await Assert.That(replies.Count).IsEqualTo(0);
        manager.Complete(new() { NotificationId = id, IsActivated = true });
        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]).IsNull();
        sink.Show(Permission("dismiss"), replies.Add);
        manager.Complete(new() { NotificationId = manager.Created[1].Id, IsCancelled = true });
        await Assert.That(replies.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Callback_failure_cannot_escape_the_native_event_boundary() {
        var manager = new Manager();
        using var sink = new NativeDesktopNotificationSink(manager, a => a(), windows: false);
        sink.Show(Permission(), _ => throw new InvalidOperationException("simulated navigation failure"));
        var shown = manager.Created.Single();
        manager.Complete(new() { NotificationId = shown.Id, ActionTag = shown.Actions.Single(a => a.Caption == "Allow").Tag });
        await Assert.That(manager.Created.Single().Closed).IsTrue();
    }

    [Test]
    public async Task Asynchronous_platform_failure_is_contained_without_replacing_the_callers_context() {
        var queued = new Queue<Action>();
        var outer = new QueueContext(queued);
        var previous = SynchronizationContext.Current;
        var manager = new Manager { AsyncFailure = true };
        using var sink = new NativeDesktopNotificationSink(manager, queued.Enqueue, windows: false);
        var escaped = new List<Exception>();
        bool restored;
        try {
            SynchronizationContext.SetSynchronizationContext(outer);
            sink.Show(Permission(), _ => { });
            restored = ReferenceEquals(SynchronizationContext.Current, outer);
            while (queued.TryDequeue(out var action)) {
                try { action(); } catch (Exception error) { escaped.Add(error); }
            }
        } finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await Assert.That(restored).IsTrue();
        await Assert.That(escaped.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Queued_action_from_a_previous_process_cannot_answer_a_reused_native_id() {
        var oldManager = new Manager();
        using var oldSink = new NativeDesktopNotificationSink(oldManager, a => a(), windows: true);
        oldSink.Show(Permission("old-process-request"), _ => { });
        var oldToast = oldManager.Created.Single();
        var staleAllow = oldToast.Actions.Single(a => a.Caption == "Allow").Tag;

        // A fresh manager models the library restarting its native numeric IDs at 1.
        var currentManager = new Manager();
        using var currentSink = new NativeDesktopNotificationSink(currentManager, a => a(), windows: true);
        var replies = new List<string?>();
        currentSink.Show(Permission("new-process-request"), replies.Add);
        var currentToast = currentManager.Created.Single();
        currentManager.Complete(new() { NotificationId = oldToast.Id, ActionTag = staleAllow });
        await Assert.That(replies.Count).IsEqualTo(0);

        currentManager.Complete(new() { NotificationId = currentToast.Id, ActionTag = currentToast.Actions.Single(a => a.Caption == "Allow").Tag });
        await Assert.That(replies.Count).IsEqualTo(1);
        await Assert.That(replies[0]).IsEqualTo("allow");
    }

    sealed class QueueContext(Queue<Action> queued) : SynchronizationContext {
        public override void Post(SendOrPostCallback callback, object? state) => queued.Enqueue(() => callback(state));
    }

    sealed class Manager : INativeNotificationManager {
        public bool AsyncFailure { get; init; }
        public List<Toast> Created { get; } = [];
        public IReadOnlyDictionary<uint, INativeNotification> ActiveNotifications => Created.ToDictionary(t => t.Id, t => (INativeNotification)t);
        public event EventHandler<NativeNotificationCompletedEventArgs>? NotificationCompleted;
        public INativeNotification CreateNotification(string? category) {
            var toast = new Toast((uint)(Created.Count + 1), category!, AsyncFailure);
            Created.Add(toast);
            return toast;
        }
        public void CloseAll() { foreach (var toast in Created) toast.Close(); }
        public void Complete(NativeNotificationCompletedEventArgs args) => NotificationCompleted?.Invoke(this, args);
    }

    sealed class Toast(uint id, string category, bool asyncFailure) : INativeNotification {
        public uint Id => id;
        public string Category => category;
        public string? Title { get; set; }
        public string? Tag { get; set; }
        public string? Message { get; set; }
        public TimeSpan? Expiration { get; set; }
        public Bitmap? Icon { get; set; }
        public string? ReplyActionTag { get; set; }
        public IReadOnlyList<NativeNotificationAction> Actions { get; private set; } = [];
        public bool Shown { get; private set; }
        public bool Closed { get; private set; }
        public void SetActions(IReadOnlyList<NativeNotificationAction> actions) => Actions = actions;
        public void Show() {
            Shown = true;
            if (asyncFailure) FailAfterYield();
        }
        static async void FailAfterYield() {
            await Task.Yield();
            throw new InvalidOperationException("simulated asynchronous platform failure");
        }
        public void Close() => Closed = true;
    }
}
