using System.Globalization;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// What a structural element becomes. Elements without a node here are transparent: their
/// content lands in the enclosing container as siblings.
static class HtmlContainers {
    public static bool HasNode(string name) => name is "details" or "blockquote" or "ul" or "ol" or "li" or "dd" or "table" or "tr" or "td" or "th";

    public static ContainerBlock Create(HtmlToken tag) => tag.Name switch {
        "details"    => new DetailsBlock { StartsOpen = tag.Attributes.ContainsKey("open") },
        "blockquote" => new QuoteBlock(null!) { QuoteChar = '>' },
        "ul"         => new ListBlock(null!) { BulletType = '-' },
        "ol"         => new ListBlock(null!) { IsOrdered = true, BulletType = '1', OrderedDelimiter = '.', DefaultOrderedStart = "1", OrderedStart = Start(tag) },
        "li"         => new ListItemBlock(null!),
        "dd"         => new HtmlIndentBlock(),
        "table"      => new Table(),
        "tr"         => new TableRow(),
        "td" or "th" => new HtmlTableCell { IsHeading = tag.Name == "th", Alignment = Alignment(tag), ColumnSpan = Span(tag) },
        _            => throw new ArgumentException($"No container for <{tag.Name}>.", nameof(tag)),
    };

    /// The two nesting rules, each blaming the element it constrains. `parent` is the nearest
    /// element with a node, null at the top of the container being folded; `child` is null for a
    /// block that is not a structural element.
    public static bool ParentAccepts(string? parent, string? child) => parent switch {
        "ul" or "ol" => child == "li",
        "table"      => child == "tr",
        "tr"         => child is "td" or "th",
        _            => true,
    };

    public static bool ChildAccepts(string? child, string? parent) => child switch {
        "li"         => parent is "ul" or "ol",
        "tr"         => parent == "table",
        "td" or "th" => parent == "tr",
        _            => true,
    };

    /// Runs once a node has all its children. A row is a header row inside `thead` or when every
    /// cell is a `th`; a table takes as many columns as its widest row, aligned as the first
    /// cell in each column says.
    public static void Finish(ContainerBlock node, bool inHeader) {
        switch (node) {
            case TableRow row:
                row.IsHeader = inHeader || (row.Count > 0 && row.All(cell => cell is HtmlTableCell { IsHeading: true }));
                break;
            case Table table:
                var columns = 0;
                foreach (var child in table) columns = Math.Max(columns, Width((TableRow)child));
                for (var i = 0; i < columns; i++) table.ColumnDefinitions.Add(new TableColumnDefinition());
                foreach (var child in table) {
                    var column = 0;
                    foreach (var cell in (TableRow)child) {
                        var html = (HtmlTableCell)cell;
                        if (html.Alignment is { } alignment && table.ColumnDefinitions[column].Alignment is null) table.ColumnDefinitions[column].Alignment = alignment;
                        column += html.ColumnSpan;
                    }
                }
                break;
        }
    }

    static int Width(TableRow row) {
        var width = 0;
        foreach (var cell in row) width += ((TableCell)cell).ColumnSpan;
        return width;
    }

    static string? Start(HtmlToken tag) => int.TryParse(tag.Attribute("start"), out var start) ? start.ToString(CultureInfo.InvariantCulture) : null;

    static int Span(HtmlToken tag) => int.TryParse(tag.Attribute("colspan"), out var span) && span > 1 ? Math.Min(span, 100) : 1;

    static TableColumnAlign? Alignment(HtmlToken tag) => tag.Attribute("align")?.Trim().ToLowerInvariant() switch {
        "left"   => TableColumnAlign.Left,
        "center" => TableColumnAlign.Center,
        "right"  => TableColumnAlign.Right,
        _        => null,
    };
}
