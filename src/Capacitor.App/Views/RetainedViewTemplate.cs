using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Templates;

namespace Capacitor.App.Views;

/// A data template that builds its view once and hands the same instance back on every later
/// build, including after the presenter has dropped it for null or another template's content.
/// On macOS a detached control stays reachable from any accessibility peer a client created for
/// it, so a template that builds afresh leaks the whole previous view on each rebuild.
public sealed class RetainedViewTemplate : DataTemplate, IRecyclingDataTemplate {
    Control? _view;

    Control? ITemplate<object?, Control?>.Build(object? param) => Build(param, null);

    Control? IRecyclingDataTemplate.Build(object? data, Control? existing) => Build(data, existing);

    new Control? Build(object? data, Control? existing) {
        // Another presenter matching this template must not steal the view from the one showing it.
        if (_view is { Parent: not null } && !ReferenceEquals(_view, existing)) return base.Build(data, null);
        return _view ??= base.Build(data, null);
    }
}
