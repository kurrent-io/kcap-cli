using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Source text for a guard that matches code shapes, with every string literal's content blanked, so a literal
/// that spells the shape can neither pass for it nor hide its absence. A char literal is matched first, or the quote in
/// <c>'"'</c> would open a string that swallows the code after it.</summary>
public static partial class SourceCode {
    [GeneratedRegex("'(?:\\\\u[0-9A-Fa-f]{4}|\\\\.|[^'\\\\\\n])'|\"\"\"[\\s\\S]*?\"\"\"|@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex Literal();

    public static string OutsideStrings(string code) => Literal().Replace(code, m => m.Value[0] == '\'' ? "' '" : "\"\"");
}
