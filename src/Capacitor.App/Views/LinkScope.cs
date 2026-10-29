namespace Capacitor.App.Views;

/// One hyperlink at a time: while a hyperlink's children are being written, no link, autolink or
/// image inside them becomes another. Shared by the renderers of one render pass.
public sealed class LinkScope {
    int _depth;

    public bool Inside => _depth > 0;

    /// The outermost hyperlink's target, which is what an image inside it opens.
    public string? Url { get; private set; }

    public void Enter(string? url) {
        if (_depth++ == 0) Url = url;
    }

    public void Exit() {
        if (--_depth == 0) Url = null;
    }
}
