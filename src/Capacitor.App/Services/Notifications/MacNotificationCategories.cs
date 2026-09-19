using System.Security.Cryptography;
using System.Text;

namespace Capacitor.App.Services.Notifications;

internal sealed class MacNotificationCategories(
    Func<string, IReadOnlyList<DesktopNotificationAction>, nint> create,
    Action<IReadOnlyCollection<nint>> register,
    Action<nint> release) : IDisposable {
    readonly Dictionary<string, (nint Handle, int Users)> _categories = new(StringComparer.Ordinal);

    public string Acquire(IReadOnlyList<DesktopNotificationAction> actions) {
        var identity = string.Concat(actions.Select(a => $"{a.Id.Length}:{a.Id}{a.Label.Length}:{a.Label}"));
        var key = "kcap." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (_categories.TryGetValue(key, out var category)) {
            _categories[key] = (category.Handle, category.Users + 1);
            return key;
        }
        var handle = create(key, actions);
        _categories.Add(key, (handle, 1));
        try { Register(); }
        catch {
            _categories.Remove(key);
            release(handle);
            throw;
        }
        return key;
    }

    public void Release(string key) {
        if (!_categories.TryGetValue(key, out var category)) return;
        if (category.Users > 1) {
            _categories[key] = (category.Handle, category.Users - 1);
            return;
        }
        _categories.Remove(key);
        try { Register(); }
        finally { release(category.Handle); }
    }

    void Register() => register(_categories.Values.Select(category => category.Handle).ToArray());

    public void Dispose() {
        if (_categories.Count == 0) return;
        var handles = _categories.Values.Select(category => category.Handle).ToArray();
        _categories.Clear();
        try { Register(); }
        finally { foreach (var handle in handles) release(handle); }
    }
}
