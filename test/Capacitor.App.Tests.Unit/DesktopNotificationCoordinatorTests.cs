using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

public class DesktopNotificationCoordinatorTests {
    sealed class Sink : IDesktopNotificationSink {
        public readonly List<DesktopNotification> Shown = [];
        public readonly Dictionary<string, Action<string?>> Callbacks = [];
        public readonly List<string> Closed = [];
        public void Show(DesktopNotification notification, Action<string?> activated) {
            Shown.Add(notification);
            Callbacks[notification.Id] = activated;
        }
        public void Close(string id) => Closed.Add(id);
        public void Dispose() { }
    }

    sealed class Harness : IDisposable {
        public readonly FakePermissionService Permissions = new();
        public readonly FakeAgentDirectory Directory = new();
        public readonly BehaviorSubject<NotificationPreferences> Preferences = new(new());
        public readonly Sink Sink = new();
        public readonly List<AgentRow> Opened = [];
        public readonly DesktopNotificationCoordinator Coordinator;
        public bool Foreground;

        public Harness(IScheduler? scheduler = null) {
            Directory.Rows.AddOrUpdate(Row());
            Coordinator = new(Permissions, Directory, Preferences, Sink, () => Foreground,
                Opened.Add, new AppNotifier(), scheduler ?? ImmediateScheduler.Instance);
        }

        public void Dispose() {
            Coordinator.Dispose();
            Preferences.Dispose();
            Permissions.Dispose();
            Directory.Dispose();
        }
    }

    static AgentRow Row(bool? waiting = false, int subagents = 0, string kind = "agent") =>
        AgentRow.FromLocal(new AgentStatusDto("a1", kind, "claude", "/repo", "Running", null, null, null,
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), null, null,
            SessionId: "s1", Title: "Fix parser", AwaitingInput: waiting, LiveSubagents: subagents),
            new RepoIdentity("/repo", "repo"));

    [Test]
    public async Task A_permission_offers_supported_actions_and_resolves_the_exact_request() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.Entry());
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        var notification = h.Sink.Shown.Single();
        await Assert.That(notification.Actions.Select(a => a.Id)).IsEquivalentTo(["allow", "always", "decline"]);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[notification.Id]("always");
        await Assert.That(h.Permissions.Resolved.Single()).IsEqualTo(("r1", PermissionAnswer.AllowAlways));
        await Assert.That(h.Sink.Closed).Contains(notification.Id);
    }

    [Test]
    public async Task Foreground_and_disabled_events_are_consumed_without_later_replay() {
        using var h = new Harness { Foreground = true };
        var entry = PermissionEntries.Entry();
        h.Permissions.Add(entry);
        h.Foreground = false;
        h.Permissions.Add(entry);
        h.Preferences.OnNext(new(Permissions: false));
        h.Permissions.Add(PermissionEntries.Entry("r2"));
        h.Preferences.OnNext(new());
        h.Permissions.Add(PermissionEntries.Entry("r2"));
        await Assert.That(h.Sink.Shown).IsEmpty();
        h.Permissions.Add(PermissionEntries.Question());
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_late_directory_row_does_not_replay_foreground_or_disabled_events() {
        foreach (var foreground in new[] { true, false }) {
            using var h = new Harness { Foreground = foreground };
            h.Directory.Rows.Clear();
            h.Preferences.OnNext(new(Permissions: foreground));
            h.Permissions.Add(PermissionEntries.Entry());
            h.Foreground = false;
            h.Preferences.OnNext(new());
            h.Directory.Rows.AddOrUpdate(Row());
            await Assert.That(h.Sink.Shown).IsEmpty();
        }
    }

    [Test]
    public async Task A_background_request_waits_for_its_directory_row() {
        using var h = new Harness();
        h.Directory.Rows.Clear();
        h.Permissions.Add(PermissionEntries.Entry());
        await Assert.That(h.Sink.Shown).IsEmpty();
        h.Directory.Rows.AddOrUpdate(Row());
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Questions_open_the_agent_without_sending_a_permission_decision() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.Question());
        var notification = h.Sink.Shown.Single();
        await Assert.That(notification.Actions.Single().Label).IsEqualTo("Respond in app");
        h.Sink.Callbacks[notification.Id]("open");
        await Assert.That(h.Opened.Single().Id).IsEqualTo("a1");
        await Assert.That(h.Permissions.Resolved).IsEmpty();
    }

    [Test]
    public async Task Replay_twins_and_settled_callbacks_do_not_repeat_or_authorize() {
        using var h = new Harness();
        var server = PermissionEntries.ServerEntry();
        h.Permissions.Add(server);
        h.Permissions.Add(PermissionEntries.Entry(serverRequestId: "srv-1"));
        h.Permissions.Cache.Remove(server.Key);
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        var notification = h.Sink.Shown.Single();
        h.Permissions.Remove("r1");
        h.Permissions.Add(PermissionEntries.Entry(serverRequestId: "srv-1"));
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        await Assert.That(h.Permissions.Resolved).IsEmpty();
        await Assert.That(h.Sink.Closed).Contains(notification.Id);
    }

    [Test]
    public async Task Disabling_a_category_withdraws_its_notifications_and_invalidates_callbacks() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.Entry());
        var notification = h.Sink.Shown.Single();
        h.Preferences.OnNext(new(Permissions: false));
        h.Sink.Callbacks[notification.Id]("decline");
        await Assert.That(h.Sink.Closed).Contains(notification.Id);
        await Assert.That(h.Permissions.Resolved).IsEmpty();
    }

    [Test]
    public async Task A_late_mapping_withdraws_the_duplicate_and_keeps_one_actionable_notification() {
        using var h = new Harness();
        var local = PermissionEntries.Entry();
        h.Permissions.Add(local);
        var server = PermissionEntries.ServerEntry();
        h.Permissions.Add(server);
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(2);
        local.ServerRequestId = "srv-1";
        h.Permissions.Cache.Refresh(local);
        h.Permissions.Cache.Remove(server.Key);
        var surviving = h.Sink.Shown.Where(n => !h.Sink.Closed.Contains(n.Id)).ToList();
        await Assert.That(surviving.Count).IsEqualTo(1);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[surviving[0].Id]("allow");
        await Assert.That(h.Permissions.Resolved.Single().RequestId).IsEqualTo("r1");
    }

    [Test]
    public async Task A_handover_keeps_the_existing_notification_and_answers_through_the_surviving_lane() {
        using var h = new Harness();
        var local = PermissionEntries.Entry(serverRequestId: "srv-1");
        h.Permissions.Add(local);
        var notification = h.Sink.Shown.Single();
        h.Permissions.Cache.Edit(cache => {
            cache.Remove(local);
            cache.AddOrUpdate(PermissionEntries.ServerEntry());
        });
        await Assert.That(h.Sink.Closed).IsEmpty();
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[notification.Id]("decline");
        await Assert.That(h.Permissions.Resolved.Single()).IsEqualTo(("srv-1", PermissionAnswer.Deny));
    }

    [Test]
    public async Task A_directory_handover_before_the_permission_handover_keeps_the_notification() {
        using var h = new Harness();
        var local = PermissionEntries.Entry(serverRequestId: "srv-1");
        h.Permissions.Add(local);
        var notification = h.Sink.Shown.Single();
        h.Directory.Rows.Edit(rows => {
            rows.RemoveKey("local:a1");
            rows.AddOrUpdate(Row() with { Key = "remote:a1", Origin = AgentOrigin.Remote });
        });
        h.Permissions.Cache.Edit(cache => {
            cache.Remove(local);
            cache.AddOrUpdate(PermissionEntries.ServerEntry());
        });
        await Assert.That(h.Sink.Closed).IsEmpty();
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Permissions.Resolved.Single()).IsEqualTo(("srv-1", PermissionAnswer.Allow));
    }

    [Test]
    public async Task A_foreign_or_stale_remote_row_cannot_preserve_a_local_notification() {
        foreach (var sameServer in new[] { false, true }) {
            using var h = new Harness();
            h.Permissions.Add(PermissionEntries.Entry(serverRequestId: "srv-1"));
            var notification = h.Sink.Shown.Single();
            h.Directory.LocalOnAppServer.OnNext(sameServer);
            h.Directory.RemoteStale.OnNext(sameServer);
            h.Directory.Rows.Edit(rows => {
                rows.RemoveKey("local:a1");
                rows.AddOrUpdate(Row() with { Key = "remote:a1", Origin = AgentOrigin.Remote });
            });
            h.Sink.Callbacks[notification.Id]("allow");
            await Assert.That(h.Sink.Closed).Contains(notification.Id);
            await Assert.That(h.Permissions.Resolved).IsEmpty();
        }
    }

    [Test]
    public async Task Acp_actions_use_offered_option_ids_and_do_not_invent_always() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.AcpPermission(options: [
            new() { OptionId = "once-42", Label = "Yes", Kind = "allow_once" },
            new() { OptionId = "reject-42", Label = "No", Kind = "reject_once" },
        ]));
        var notification = h.Sink.Shown.Single();
        await Assert.That(notification.Actions.Select(a => a.Id)).IsEquivalentTo(["allow", "decline"]);
        h.Sink.Callbacks[notification.Id]("always");
        await Assert.That(h.Permissions.Picked).IsEmpty();
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Permissions.Picked.Single()).IsEqualTo(("srv-1", "once-42"));
    }

    [Test]
    public async Task A_local_acp_request_offering_only_a_standing_grant_has_no_once_button() {
        using var h = new Harness();
        h.Permissions.Add(new PendingPermissionRequest(new PermissionPendingDto(
            "acp-1", "a1", "s1", "pi", "Bash", null, null, false, false, "2026-09-19T12:00:00Z",
            SupportsAllowOnce: false, SupportsAllowAlways: true)));
        var notification = h.Sink.Shown.Single();
        await Assert.That(notification.Actions.Select(a => a.Id)).IsEquivalentTo(["always", "decline"]);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[notification.Id]("always");
        await Assert.That(h.Permissions.Resolved.Single()).IsEqualTo(("acp-1", PermissionAnswer.AllowAlways));
    }

    [Test]
    public async Task Questions_and_idle_switches_do_not_disable_permission_alerts() {
        using var h = new Harness();
        h.Preferences.OnNext(new(Questions: false, Idle: false));
        h.Permissions.Add(PermissionEntries.Question());
        h.Directory.Rows.AddOrUpdate(Row(true));
        h.Permissions.Add(PermissionEntries.Entry());
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        await Assert.That(h.Sink.Shown.Single().Actions.Select(a => a.Id)).Contains("allow");
    }

    [Test]
    public async Task A_local_daemon_on_another_server_cannot_claim_a_remote_requests_notification() {
        using var h = new Harness();
        h.Directory.LocalOnAppServer.OnNext(false);
        h.Directory.Rows.AddOrUpdate(Row() with { Key = "remote:a2", Origin = AgentOrigin.Remote, Id = "a2" });
        h.Permissions.Add(PermissionEntries.Entry(serverRequestId: "srv-1"));
        h.Permissions.Add(PermissionEntries.ServerEntry());
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(2);
        h.Permissions.Queue(PermissionResolveKind.Applied);
        h.Sink.Callbacks[h.Sink.Shown[1].Id]("allow");
        await Assert.That(h.Permissions.Resolved.Single().RequestId).IsEqualTo("srv-1");
        await Assert.That(h.Permissions.Cache.Lookup("local:r1").HasValue).IsTrue();
    }

    [Test]
    public async Task A_click_queued_before_a_settlement_still_checks_the_live_permission_cache() {
        var scheduler = new HistoricalScheduler();
        using var h = new Harness(scheduler);
        scheduler.Start();
        h.Permissions.Add(PermissionEntries.Entry());
        scheduler.Start();
        var notification = h.Sink.Shown.Single();
        h.Sink.Callbacks[notification.Id]("allow");
        h.Permissions.Remove("r1");
        scheduler.Start();
        await Assert.That(h.Permissions.Resolved).IsEmpty();
    }

    [Test]
    public async Task Shutdown_withdraws_all_notifications_and_ignores_late_actions() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.Entry());
        var notification = h.Sink.Shown.Single();
        h.Coordinator.Dispose();
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Sink.Closed).Contains(notification.Id);
        await Assert.That(h.Permissions.Resolved).IsEmpty();
    }

    [Test]
    public async Task Idle_is_an_edge_and_rearms_after_the_next_working_turn() {
        using var h = new Harness();
        h.Directory.Rows.AddOrUpdate(Row(true));
        var notification = h.Sink.Shown.Single();
        await Assert.That(notification.Actions.Single().Label).IsEqualTo("Open agent");
        h.Directory.Rows.AddOrUpdate(Row(true) with { Title = "New title" });
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        h.Sink.Callbacks[notification.Id](null);
        await Assert.That(h.Opened.Single().Id).IsEqualTo("a1");
        h.Directory.Rows.AddOrUpdate(Row(false));
        h.Directory.Rows.AddOrUpdate(Row(true));
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Unknown_initial_idle_pending_requests_and_running_subagents_do_not_notify_idle() {
        using var h = new Harness();
        h.Directory.Rows.Clear();
        h.Directory.Rows.AddOrUpdate(Row(true));
        h.Directory.Rows.AddOrUpdate(Row(null));
        h.Directory.Rows.AddOrUpdate(Row(true));
        await Assert.That(h.Sink.Shown).IsEmpty();
        h.Directory.Rows.AddOrUpdate(Row(false));
        h.Permissions.Add(PermissionEntries.Entry());
        h.Directory.Rows.AddOrUpdate(Row(true));
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        h.Permissions.Remove("r1");
        h.Directory.Rows.AddOrUpdate(Row(false));
        h.Directory.Rows.AddOrUpdate(Row(true, subagents: 1));
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(1);
        h.Directory.Rows.AddOrUpdate(Row(true));
        await Assert.That(h.Sink.Shown.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_transport_failure_opens_the_request_in_app_and_duplicate_clicks_are_serialized() {
        using var h = new Harness();
        h.Permissions.Add(PermissionEntries.Entry());
        var notification = h.Sink.Shown.Single();
        h.Permissions.Queue(PermissionResolveKind.TransportFailure, "offline");
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Opened.Single().Id).IsEqualTo("a1");
        await Assert.That(h.Permissions.Cache.Count).IsEqualTo(1);
        h.Sink.Callbacks[notification.Id]("allow");
        await Assert.That(h.Permissions.Resolved.Count).IsEqualTo(1);
    }
}
