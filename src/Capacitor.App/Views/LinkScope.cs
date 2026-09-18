namespace Capacitor.App.Views;

/// One hyperlink at a time: while a hyperlink's children are being written, no link, autolink or
/// image inside them becomes another. Shared by the renderers of one render pass.
public sealed class LinkScope {
    int _depth;

    public bool Inside => _depth > 0;

    public void Enter() => _depth++;

    public void Exit() => _depth--;
}
