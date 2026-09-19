using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using static Capacitor.App.Services.Notifications.MacNotificationInterop;

namespace Capacitor.App.Services.Notifications;

/// UserNotifications requires a real application bundle. A development `dotnet run` intentionally
/// has no native sink; calling currentNotificationCenter from an unbundled process can abort it.
[SupportedOSPlatform("macos")]
internal sealed class MacOsDesktopNotificationSink : IDesktopNotificationSink {
    sealed record Pending(DesktopNotification Source, string NativeId, string Category, Action<string?> Activated);

    static readonly ConcurrentDictionary<nint, WeakReference<MacOsDesktopNotificationSink>> Delegates = new();
    static nint _delegateClass;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, nint> _categories = new(StringComparer.Ordinal);
    readonly nint _center;
    readonly nint _delegate;
    bool _disposed;

    public MacOsDesktopNotificationSink() {
        if (!OperatingSystem.IsMacOSVersionAtLeast(11)) return;
        try {
            using var pool = new Pool();
            var bundle = Send(GetClass("NSBundle"), Selector("mainBundle"));
            var identifier = Text(Send(bundle, Selector("bundleIdentifier")));
            var path = Text(Send(bundle, Selector("bundlePath")));
            if (string.IsNullOrEmpty(identifier) || path?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) != true) return;

            _center = Send(GetClass("UNUserNotificationCenter"), Selector("currentNotificationCenter"));
            if (_center == 0) return;
            _delegate = Send(DelegateClass(), Selector("new"));
            if (_delegate == 0) throw new InvalidOperationException("Unable to initialize the notification delegate.");
            Delegates[_delegate] = new(this);
            SendVoid(_center, Selector("setDelegate:"), _delegate);
            // Nothing from a previous process can still answer a live request.
            SendVoid(_center, Selector("removeAllPendingNotificationRequests"));
            SendVoid(_center, Selector("removeAllDeliveredNotifications"));
        } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }

    public void Show(DesktopNotification notification, Action<string?> activated) {
        if (_disposed || _center == 0 || _delegate == 0) return;
        Close(notification.Id);
        try {
            using var pool = new Pool();
            var category = Category(notification.Actions);
            var pending = new Pending(notification, Guid.NewGuid().ToString("N"), category, activated);
            _pending.Add(notification.Id, pending);
            using var block = MacNotificationBlock.Authorization((granted, error) => {
                var failure = Error(error);
                Post(() => {
                    if (!IsCurrent(pending)) return;
                    if (!granted || failure is not null) {
                        Close(notification.Id);
                        if (failure is not null) NativeDesktopNotificationSink.Report(failure);
                        return;
                    }
                    Publish(pending);
                });
            });
            SendVoid(_center, Selector("requestAuthorizationWithOptions:completionHandler:"), 4, block.Handle);
        } catch (Exception error) {
            Close(notification.Id);
            NativeDesktopNotificationSink.Report(error);
        }
    }

    void Publish(Pending pending) {
        using var pool = new Pool();
        var content = Send(GetClass("UNMutableNotificationContent"), Selector("new"));
        try {
            SendVoid(content, Selector("setTitle:"), String(pending.Source.Title));
            SendVoid(content, Selector("setBody:"), String(pending.Source.Body));
            SendVoid(content, Selector("setCategoryIdentifier:"), String(pending.Category));
            var request = Send(GetClass("UNNotificationRequest"), Selector("requestWithIdentifier:content:trigger:"), String(pending.NativeId), content, 0);
            using var block = MacNotificationBlock.Completion(error => {
                var failure = Error(error);
                Post(() => {
                    // Withdrawal may race the asynchronous add. Withdraw again after it finishes.
                    if (!IsCurrent(pending)) { Withdraw(pending.NativeId); return; }
                    if (failure is null) return;
                    Close(pending.Source.Id);
                    NativeDesktopNotificationSink.Report(failure);
                });
            });
            SendVoid(_center, Selector("addNotificationRequest:withCompletionHandler:"), request, block.Handle);
        } finally { Release(content); }
    }

    string Category(IReadOnlyList<DesktopNotificationAction> actions) {
        var identity = string.Concat(actions.Select(a => $"{a.Id.Length}:{a.Id}{a.Label.Length}:{a.Label}"));
        var key = "kcap." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (_categories.ContainsKey(key)) return key;
        var nativeActions = Array(actions.Select(a => Send(GetClass("UNNotificationAction"), Selector("actionWithIdentifier:title:options:"),
            String(key + "." + a.Id), String(a.Label), a.Id == "open" ? 4 : 0)));
        var category = Send(GetClass("UNNotificationCategory"), Selector("categoryWithIdentifier:actions:intentIdentifiers:options:"),
            String(key), nativeActions, Array([]), 1); // custom-dismiss callback
        Send(category, Selector("retain"));
        _categories.Add(key, category);
        var set = Send(GetClass("NSSet"), Selector("setWithArray:"), Array(_categories.Values));
        SendVoid(_center, Selector("setNotificationCategories:"), set);
        return key;
    }

    bool IsCurrent(Pending pending) => !_disposed && _pending.TryGetValue(pending.Source.Id, out var current) && ReferenceEquals(current, pending);

    void Respond(string nativeId, string? nativeAction) {
        var pending = _pending.Values.FirstOrDefault(p => p.NativeId == nativeId);
        if (_disposed || pending is null) return;
        if (nativeAction == "com.apple.UNNotificationDismissActionIdentifier") { Close(pending.Source.Id); return; }
        string? action = null;
        if (nativeAction != "com.apple.UNNotificationDefaultActionIdentifier") {
            action = pending.Source.Actions.FirstOrDefault(a => pending.Category + "." + a.Id == nativeAction)?.Id;
            if (action is null) return;
        }
        Close(pending.Source.Id);
        pending.Activated(action);
    }

    public void Close(string id) {
        if (!_pending.Remove(id, out var pending)) return;
        try { Withdraw(pending.NativeId); } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }

    void Withdraw(string nativeId) {
        using var pool = new Pool();
        var identifiers = Array([String(nativeId)]);
        SendVoid(_center, Selector("removePendingNotificationRequestsWithIdentifiers:"), identifiers);
        SendVoid(_center, Selector("removeDeliveredNotificationsWithIdentifiers:"), identifiers);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _pending.Keys.ToArray()) Close(id);
        if (_delegate == 0) return;
        Delegates.TryRemove(_delegate, out _);
        if (Send(_center, Selector("delegate")) == _delegate) SendVoid(_center, Selector("setDelegate:"), 0);
        Release(_delegate);
        foreach (var category in _categories.Values) Release(category);
        _categories.Clear();
    }

    static void Post(Action action) {
        try {
            Dispatcher.UIThread.Post(() => {
                try { action(); } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
            });
        } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }

    static unsafe nint DelegateClass() {
        if (_delegateClass != 0) return _delegateClass;
        var cls = AllocateClass(GetClass("NSObject"), "KcapUserNotificationCenterDelegate", 0);
        if (cls == 0) throw new InvalidOperationException("Unable to create the notification delegate.");
        if (AddProtocol(cls, GetProtocol("UNUserNotificationCenterDelegate")) == 0 ||
            AddMethod(cls, Selector("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@") == 0 ||
            AddMethod(cls, Selector("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidRespond, "v@:@@@") == 0)
            throw new InvalidOperationException("Unable to register notification callbacks.");
        RegisterClass(cls);
        return _delegateClass = cls;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) {
        // macOS calls this only while our app is foreground. Suppress a notification whose
        // asynchronous authorization/delivery raced the user bringing the app forward.
        try { MacNotificationBlock.Present(completion, 0); }
        catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void DidRespond(nint self, nint selector, nint center, nint response, nint completion) {
        try {
            if (!Delegates.TryGetValue(self, out var reference) || !reference.TryGetTarget(out var sink)) return;
            var notification = Send(response, Selector("notification"));
            var request = Send(notification, Selector("request"));
            var id = Text(Send(request, Selector("identifier")));
            var action = Text(Send(response, Selector("actionIdentifier")));
            if (id is not null) Post(() => sink.Respond(id, action));
        } catch (Exception error) { NativeDesktopNotificationSink.Report(error); }
        finally { MacNotificationBlock.Finish(completion); }
    }
}
