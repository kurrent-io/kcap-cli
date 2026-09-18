using System.Globalization;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// The pixel size an img tag asks for, carried on the image inline for its renderer. Only
/// plain pixel values count: a percentage or `auto` is relative to a box this renderer does not
/// have.
public sealed record ImageSize(double? Width, double? Height) {
    static readonly object Key = new();

    public static ImageSize? From(HtmlToken tag) {
        var width = Pixels(tag.Attribute("width"));
        var height = Pixels(tag.Attribute("height"));
        return width is null && height is null ? null : new(width, height);
    }

    public static ImageSize? Of(LinkInline image) => image.GetData(Key) as ImageSize;

    public void Attach(LinkInline image) => image.SetData(Key, this);

    static double? Pixels(string? value) {
        if (value is null) return null;
        var text = value.Trim();
        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels > 0 && pixels <= 4096 ? pixels : null;
    }
}
