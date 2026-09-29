using System.Security;
using Avalonia;
using Avalonia.Labs.Notifications;
using Avalonia.Threading;

namespace Capacitor.App.Services.Notifications;

public sealed class NativeDesktopNotificationSink : IDesktopNotificationSink, IDesktopNotificationAccess {
    static bool _labsAvailable;
    readonly IDesktopNotificationSink _backend;

    public NativeDesktopNotificationSink() {
        _backend = OperatingSystem.IsMacOS()
            ? new MacOsDesktopNotificationSink()
            : new LabsSink(_labsAvailable ? NativeNotificationManager.Current : null, a => Dispatcher.UIThread.Post(a), OperatingSystem.IsWindows());
    }

    internal NativeDesktopNotificationSink(INativeNotificationManager manager, Action<Action> dispatch, bool windows) =>
        _backend = new LabsSink(manager, dispatch, windows);

    public void Show(DesktopNotification notification, Action<string?> activated) => _backend.Show(notification, activated);
    public void Close(string id) => _backend.Close(id);
    public void Dispose() => _backend.Dispose();

    public Task<DesktopNotificationAccess> GetAsync() =>
        (_backend as IDesktopNotificationAccess)?.GetAsync() ?? Task.FromResult(DesktopNotificationAccess.Unknown);
    public Task<DesktopNotificationAccess> RequestAsync() =>
        (_backend as IDesktopNotificationAccess)?.RequestAsync() ?? Task.FromResult(DesktopNotificationAccess.Unknown);
    public void OpenSystemSettings() => (_backend as IDesktopNotificationAccess)?.OpenSystemSettings();

    public static AppBuilder Configure(AppBuilder builder) {
        // Labs' macOS implementation has async-void authorization failures and only withdraws
        // pending notifications. Use our UserNotifications adapter there instead.
        if (OperatingSystem.IsMacOS()) return builder;

        try {
            // Capture the library's setup callback on an inert builder, so failures during its
            // deferred COM/platform registration cannot abort the real application's startup.
            var registration = AppBuilder.Configure<Application>().WithAppNotifications(new AppNotificationOptions {
                Channels = [new NotificationChannel("attention", "Agent attention", NotificationPriority.High)],
                ClearOnAppClose = true,
                AppUserModelId = "io.kurrent.capacitor",
                AppName = "Kurrent Capacitor",
            });
            return builder.AfterSetup(app => {
                try {
                    GuardAsyncVoid(() => registration.AfterSetupCallback(app), a => Dispatcher.UIThread.Post(a));
                    GuardAsyncVoid(() => NativeNotificationManager.Current?.CloseAll(), a => Dispatcher.UIThread.Post(a));
                    _labsAvailable = true;
                } catch (Exception error) {
                    _labsAvailable = false;
                    Report(error);
                }
            });
        } catch (Exception error) {
            Report(error);
            return builder;
        }
    }

    internal static void Report(Exception error) => Report(error.Message);

    internal static void Report(string message) {
        // Never let logging failures propagate across an Objective-C/COM callback boundary.
        try { Console.Error.WriteLine($"Desktop notifications: {message}"); } catch (Exception) { }
    }

    // The optional Linux Labs backend exposes async-void Initialize/Show/Close. A scoped context
    // catches its asynchronously posted exceptions without changing the application's UI context.
    static void GuardAsyncVoid(Action action, Action<Action> dispatch) {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NotificationContext(dispatch));
        try { action(); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    sealed class NotificationContext(Action<Action> dispatch) : SynchronizationContext {
        public override void Post(SendOrPostCallback callback, object? state) {
            try {
                dispatch(() => {
                    try { callback(state); } catch (Exception error) { Report(error); }
                });
            } catch (Exception error) { Report(error); }
        }
    }

    sealed class LabsSink : IDesktopNotificationSink {
        sealed record Pending(DesktopNotification Source, INativeNotification Native,
            IReadOnlyDictionary<string, string> ActionIdsByTag, Action<string?> Activated);

        readonly INativeNotificationManager? _manager;
        readonly Action<Action> _dispatch;
        readonly bool _windows;
        readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
        bool _disposed;

        public LabsSink(INativeNotificationManager? manager, Action<Action> dispatch, bool windows) {
            _manager = manager;
            _dispatch = dispatch;
            _windows = windows;
            if (manager is not null) manager.NotificationCompleted += Completed;
        }

        public void Show(DesktopNotification notification, Action<string?> activated) {
            if (_disposed || _manager is null) return;
            Close(notification.Id);
            try {
                var native = _manager.CreateNotification("attention");
                if (native is null) return;
                // Labs 12.0.2 directly interpolates these strings into Windows toast XML.
                native.Title = _windows ? SecurityElement.Escape(notification.Title) : notification.Title;
                native.Message = _windows ? SecurityElement.Escape(notification.Body) : notification.Body;
                native.Tag = Guid.NewGuid().ToString("N");
                // Labs' numeric IDs restart at 1 in a new process. A queued COM action from an
                // old toast must not answer a new request that happens to reuse its numeric ID.
                var actions = notification.Actions.Select(a =>
                    (Source: a, Native: new NativeNotificationAction(_windows ? SecurityElement.Escape(a.Label) : a.Label,
                        Guid.NewGuid().ToString("N")))).ToArray();
                native.SetActions(actions.Select(a => a.Native).ToArray());
                var actionIds = actions.ToDictionary(a => a.Native.Tag, a => a.Source.Id, StringComparer.Ordinal);
                _pending.Add(notification.Id, new(notification, native, actionIds, activated));
                GuardAsyncVoid(native.Show, _dispatch);
            } catch (Exception error) {
                Close(notification.Id);
                Report(error);
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
                        // Linux marks buttons as activated too; inspect the action first.
                        string? actionId = null;
                        if (args.ActionTag is { } tag) {
                            if (!pending.ActionIdsByTag.TryGetValue(tag, out actionId)) return;
                        } else if (!args.IsActivated) return;

                        Close(pending.Source.Id);
                        pending.Activated(actionId);
                    } catch (Exception error) { Report(error); }
                });
            } catch (Exception error) { Report(error); }
        }

        public void Close(string id) {
            if (!_pending.Remove(id, out var pending)) return;
            try { GuardAsyncVoid(pending.Native.Close, _dispatch); } catch (Exception error) { Report(error); }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            if (_manager is not null) _manager.NotificationCompleted -= Completed;
            foreach (var id in _pending.Keys.ToArray()) Close(id);
        }
    }
}
