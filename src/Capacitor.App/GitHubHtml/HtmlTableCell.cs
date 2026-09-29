using Markdig.Extensions.Tables;

namespace Capacitor.App.GitHubHtml;

/// A `td` or `th`: what the table needs from the tag once its rows are assembled.
public sealed class HtmlTableCell : TableCell {
    public bool IsHeading { get; init; }

    public TableColumnAlign? Alignment { get; init; }
}
