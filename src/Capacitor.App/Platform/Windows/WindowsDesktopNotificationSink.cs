using System.Security;
using Avalonia;
using Avalonia.Labs.Notifications;
using Avalonia.Threading;

using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Windows;

/// Windows toasts through Avalonia.Labs, which interpolates titles, bodies and labels into toast
/// XML unescaped, and exposes async-void Initialize/Show/Close.
internal sealed class WindowsDesktopNotificationSink : IDesktopNotificationSink, IDesktopNotificationAccess {
    const string AppUserModelId = "io.kurrent.kcap";

    sealed record Pending(DesktopNotification Source, INativeNotification Native,
        IReadOnlyDictionary<string, string> ActionIdsByTag, Action<string?> Activated);

    static bool _available;

    readonly INativeNotificationManager? _manager;
    readonly Action<Action> _dispatch;
    readonly IDesktopNotificationAccess _access = UnknownDesktopNotificationAccess.Instance;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    bool _disposed;

    public WindowsDesktopNotificationSink(Action<Action> dispatch)
        : this(_available ? NativeNotificationManager.Current : null, dispatch) {
        if (OperatingSystem.IsWindows()) _access = new WindowsDesktopNotificationAccess(AppUserModelId);
    }

    internal WindowsDesktopNotificationSink(INativeNotificationManager? manager, Action<Action> dispatch) {
        _manager = manager;
        _dispatch = dispatch;
        if (manager is not null) manager.NotificationCompleted += Completed;
    }

    public static AppBuilder Configure(AppBuilder builder) {
        try {
            // Capture the library's setup callback on an inert builder, so failures during its
            // deferred COM registration cannot abort the real application's startup.
            var registration = AppBuilder.Configure<Application>().WithAppNotifications(new AppNotificationOptions {
                Channels = [new NotificationChannel("attention", "Agent attention", NotificationPriority.High)],
                ClearOnAppClose = true,
                AppUserModelId = AppUserModelId,
                AppName = "kcap",
            });
            return builder.AfterSetup(app => {
                try {
                    GuardAsyncVoid(() => registration.AfterSetupCallback(app), a => Dispatcher.UIThread.Post(a));
                    GuardAsyncVoid(() => NativeNotificationManager.Current?.CloseAll(), a => Dispatcher.UIThread.Post(a));
                    _available = true;
                } catch (Exception error) {
                    _available = false;
                    DesktopNotifications.Report(error);
                }
            });
        } catch (Exception error) {
            DesktopNotifications.Report(error);
            return builder;
        }
    }

    public void Show(DesktopNotification notification, Action<string?> activated) {
        if (_disposed || _manager is null) return;
        Close(notification.Id);
        try {
            var native = _manager.CreateNotification("attention");
            if (native is null) return;
            native.Title = SecurityElement.Escape(notification.Title);
            native.Message = SecurityElement.Escape(notification.Body);
            native.Tag = Guid.NewGuid().ToString("N");
            // Labs' numeric IDs restart at 1 in a new process. A queued COM action from an
            // old toast must not answer a new request that happens to reuse its numeric ID.
            var actions = notification.Actions.Select(a =>
                (Source: a, Native: new NativeNotificationAction(SecurityElement.Escape(a.Label), Guid.NewGuid().ToString("N")))).ToArray();
            native.SetActions(actions.Select(a => a.Native).ToArray());
            var actionIds = actions.ToDictionary(a => a.Native.Tag, a => a.Source.Id, StringComparer.Ordinal);
            _pending.Add(notification.Id, new(notification, native, actionIds, activated));
            GuardAsyncVoid(native.Show, _dispatch);
        } catch (Exception error) {
            Close(notification.Id);
            DesktopNotifications.Report(error);
        }
    }

    void Completed(object? sender, NativeNotificationCompletedEventArgs args) {
        try {
            _dispatch(() => {
                try {
                    if (_disposed) return;
                    var pending = _pending.Values.FirstOrDefault(p => p.Native.Id == args.NotificationId);
                    if (pending is null) return;
                    if (args.IsCancelled) { Close(pending.Source.Id); return; }
                    string? actionId = null;
                    if (args.ActionTag is { } tag) {
                        if (!pending.ActionIdsByTag.TryGetValue(tag, out actionId)) return;
                    } else if (!args.IsActivated) return;

                    Close(pending.Source.Id);
                    pending.Activated(actionId);
                } catch (Exception error) { DesktopNotifications.Report(error); }
            });
        } catch (Exception error) { DesktopNotifications.Report(error); }
    }

    public Task<DesktopNotificationAccess> GetAsync() => _access.GetAsync();
    public Task<DesktopNotificationAccess> RequestAsync() => _access.RequestAsync();
    public void OpenSystemSettings() => _access.OpenSystemSettings();

    public void Close(string id) {
        if (!_pending.Remove(id, out var pending)) return;
        try { GuardAsyncVoid(pending.Native.Close, _dispatch); } catch (Exception error) { DesktopNotifications.Report(error); }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null) _manager.NotificationCompleted -= Completed;
        foreach (var id in _pending.Keys.ToArray()) Close(id);
    }

    // A scoped context catches Labs' asynchronously posted exceptions without changing the
    // application's UI context.
    static void GuardAsyncVoid(Action action, Action<Action> dispatch) {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NotificationContext(dispatch));
        try { action(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    sealed class NotificationContext(Action<Action> dispatch) : SynchronizationContext {
        public override void Post(SendOrPostCallback callback, object? state) {
            try {
                dispatch(() => {
                    try { callback(state); } catch (Exception error) { DesktopNotifications.Report(error); }
                });
            } catch (Exception error) { DesktopNotifications.Report(error); }
        }
    }
}
