using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Capacitor.App.Services.Notifications;

/// Asks for notification access while an app window is active. Left to the first notification,
/// the request lands while the app is in the background, where the operating system's prompt
/// reads as an ordinary banner and an unanswered one settles as denied.
public sealed class DesktopNotificationAccessPrompt : IDisposable {
    readonly IDesktopNotificationAccess _access;
    readonly Func<bool> _isForeground;
    readonly CompositeDisposable _subscriptions = new();
    NotificationPreferences? _preferences;
    bool _asked;
    bool _disposed;

    public DesktopNotificationAccessPrompt(IDesktopNotificationAccess access, IObservable<NotificationPreferences> preferences,
            IObservable<Unit> windowActivated, Func<bool> isForeground, IScheduler scheduler) {
        _access = access;
        _isForeground = isForeground;
        _subscriptions.Add(preferences.ObserveOn(scheduler).Subscribe(value => {
            _preferences = value;
            _ = AskOnceAsync();
        }));
        _subscriptions.Add(windowActivated.ObserveOn(scheduler).Subscribe(activation => { _ = AskOnceAsync(); }));
    }

    async Task AskOnceAsync() {
        if (_disposed || _asked || _preferences is not { } wanted ||
            !(wanted.Permissions || wanted.Questions || wanted.Idle) || !_isForeground()) return;
        _asked = true;
        try {
            if (await _access.GetAsync() != DesktopNotificationAccess.NotDetermined) return;
            // The read is asynchronous: the user may have left the app while it ran.
            if (_disposed || !_isForeground()) {
                _asked = false;
                return;
            }
            await _access.RequestAsync();
        } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }

    public void Dispose() {
        _disposed = true;
        _subscriptions.Dispose();
    }
}
