using System.Security;
using Tmds.DBus.Protocol;

using Capacitor.App.Services.Notifications;

namespace Capacitor.App.Platform.Linux;

/// Calls org.freedesktop.Notifications directly. Avalonia.Labs' Linux backend is compiled against an
/// older Tmds.DBus.Protocol than Avalonia ships and fails at runtime with a missing method.
/// Every member runs on the UI thread; DBus callbacks are marshalled back through dispatch.
sealed class LinuxDesktopNotificationSink(Action<Action> dispatch) : IDesktopNotificationSink {
    const string Service       = "org.freedesktop.Notifications";
    const string Path          = "/org/freedesktop/Notifications";
    const string DefaultAction = "default";

    sealed class Pending(IReadOnlyDictionary<string, string> actionIdsByKey, Action<string?> activated) {
        public IReadOnlyDictionary<string, string> ActionIdsByKey { get; } = actionIdsByKey;
        public Action<string?> Activated { get; } = activated;
        public uint? NativeId { get; set; }
    }

    // Resolved per call, inside each guarded call, so a missing session bus never fails construction.
    static DBusConnection Connection => DBusConnection.Session;
    readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    readonly List<IDisposable> _watches = [];
    Task<(bool Actions, bool Markup)>? _ready;
    bool _disposed;

    public void Show(DesktopNotification notification, Action<string?> activated) {
        if (_disposed) return;
        Close(notification.Id);

        // Keys are fresh per notification, so an action from an older one never answers this one.
        var actions = notification.Actions.Select(a => (Key: Guid.NewGuid().ToString("N"), a.Label, a.Id)).ToArray();
        var pending = new Pending(actions.ToDictionary(a => a.Key, a => a.Id, StringComparer.Ordinal), activated);
        _pending.Add(notification.Id, pending);

        _ = ShowAsync(notification, actions, pending);
    }

    async Task ShowAsync(DesktopNotification notification, (string Key, string Label, string Id)[] actions, Pending pending) {
        try {
            var (buttons, markup) = await EnsureReadyAsync();
            var body = buttons || actions.Length == 0 ? notification.Body : $"{notification.Body}\n\nOpen kcap to respond.";
            string[] wire = buttons ? [DefaultAction, "", .. actions.SelectMany(a => new[] { a.Key, a.Label })] : [];
            // A request with choices must outlive the server's default timeout, whose close drops it.
            var expire = actions.Length > 0 ? 0 : -1;
            // Only servers advertising body-markup parse the body; the rest show escapes literally.
            var id = await Connection.CallMethodAsync(
                Notify(notification.Title, markup ? SecurityElement.Escape(body) : body, wire, expire), (m, _) => m.GetBodyReader().ReadUInt32());
            dispatch(() => {
                if (!_disposed && _pending.ContainsValue(pending)) pending.NativeId = id;
                else CloseNative(id);
            });
        } catch (Exception error) {
            dispatch(() => { if (_pending.ContainsValue(pending)) _pending.Remove(notification.Id); });
            DesktopNotifications.Report(error);
        }
    }

    // A failed connection is not kept, so the next notification tries again.
    Task<(bool Actions, bool Markup)> EnsureReadyAsync() {
        if (_ready is { IsFaulted: true } or { IsCanceled: true }) _ready = null;
        return _ready ??= ConnectAsync();
    }

    /// Subscribes to the server's signals and answers which of action buttons and body markup it supports.
    async Task<(bool Actions, bool Markup)> ConnectAsync() {
        var watches = new List<IDisposable>();
        try {
            watches.Add(await Connection.WatchSignalAsync(null, Path, Service, "ActionInvoked",
                (m, _) => { var r = m.GetBodyReader(); return (Id: r.ReadUInt32(), Key: r.ReadString()); },
                n => { if (n.HasValue) { var (id, key) = n.Value; dispatch(() => Invoked(id, key)); } },
                ObserverFlags.None, emitOnCapturedContext: false));
            watches.Add(await Connection.WatchSignalAsync(null, Path, Service, "NotificationClosed",
                (m, _) => m.GetBodyReader().ReadUInt32(),
                n => { if (n.HasValue) { var id = n.Value; dispatch(() => Remove(id)); } },
                ObserverFlags.None, emitOnCapturedContext: false));
            var capabilities = await Connection.CallMethodAsync(GetCapabilities(), (m, _) => m.GetBodyReader().ReadArrayOfString());
            if (_disposed) {
                foreach (var watch in watches) watch.Dispose();
                return (false, false);
            }
            _watches.AddRange(watches);
            return (capabilities.Contains("actions"), capabilities.Contains("body-markup"));
        } catch {
            foreach (var watch in watches) watch.Dispose();
            throw;
        }
    }

    void Invoked(uint nativeId, string key) {
        if (_disposed || Find(nativeId) is not var (id, pending)) return;

        string? actionId = null;
        if (key != DefaultAction && !pending.ActionIdsByKey.TryGetValue(key, out actionId)) return;

        _pending.Remove(id);
        CloseNative(nativeId);
        try { pending.Activated(actionId); } catch (Exception error) { DesktopNotifications.Report(error); }
    }

    void Remove(uint nativeId) {
        if (Find(nativeId) is var (id, _)) _pending.Remove(id);
    }

    (string Id, Pending Pending)? Find(uint nativeId) {
        foreach (var (id, pending) in _pending)
            if (pending.NativeId == nativeId) return (id, pending);
        return null;
    }

    public void Close(string id) {
        if (_pending.Remove(id, out var pending) && pending.NativeId is { } nativeId) CloseNative(nativeId);
    }

    static void CloseNative(uint nativeId) => _ = CloseNativeAsync(nativeId);

    static async Task CloseNativeAsync(uint nativeId) {
        try {
            await Connection.CallMethodAsync(CloseNotification(nativeId));
        } catch (Exception error) {
            DesktopNotifications.Report(error);
        }
    }

    public void Dispose() {
        if (_disposed) return;
        foreach (var id in _pending.Keys.ToArray()) Close(id);
        _disposed = true;
        foreach (var watch in _watches) watch.Dispose();
    }

    static MessageBuffer Notify(string title, string body, string[] actions, int expire) {
        var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, Path, Service, "Notify", "susssasa{sv}i");
        writer.WriteString("kcap");
        writer.WriteUInt32(0);
        writer.WriteString("");
        writer.WriteString(title);
        writer.WriteString(body);
        writer.WriteArray(actions);
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        writer.WriteInt32(expire);
        return writer.CreateMessage();
    }

    static MessageBuffer GetCapabilities() {
        var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, Path, Service, "GetCapabilities");
        return writer.CreateMessage();
    }

    static MessageBuffer CloseNotification(uint nativeId) {
        var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, Path, Service, "CloseNotification", "u");
        writer.WriteUInt32(nativeId);
        return writer.CreateMessage();
    }
}
