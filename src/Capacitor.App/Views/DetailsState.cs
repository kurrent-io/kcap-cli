namespace Capacitor.App.Views;

/// Which details sections of one view the user has toggled, by ordinal. A section nobody touched
/// follows its `open` attribute.
public sealed class DetailsState {
    readonly Dictionary<int, bool> _expanded = new();

    public bool IsExpanded(int ordinal, bool isOpen) => _expanded.TryGetValue(ordinal, out var expanded) ? expanded : isOpen;

    public void Set(int ordinal, bool expanded) => _expanded[ordinal] = expanded;

    public void Clear() => _expanded.Clear();
}
