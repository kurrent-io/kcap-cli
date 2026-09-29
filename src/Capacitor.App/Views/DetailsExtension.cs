using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// Per view, because the details state is the view's.
public sealed class DetailsExtension(DetailsState state, Action<int, bool> toggle) : IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) => renderer.ObjectRenderers.Add(new DetailsBlockRenderer(state, toggle));
}
