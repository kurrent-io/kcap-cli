using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Source text for a guard that matches code shapes, with every comment removed and every string literal's content
/// blanked, so neither can spell the shape or hide its absence. One left-to-right scan takes whichever starts first, so a
/// quote inside a comment or a char literal such as <c>'"'</c> never opens a string that swallows the code after it.</summary>
public static partial class SourceCode {
    [GeneratedRegex("//[^\\n]*|/\\*[\\s\\S]*?\\*/|'(?:\\\\u[0-9A-Fa-f]{4}|\\\\.|[^'\\\\\\n])'|(?<q>\"{3,})[\\s\\S]*?\\k<q>(?!\")|@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex Token();

    public static string WithoutCommentsOrStrings(string code) =>
        Token().Replace(code, m => m.Value[0] switch {
            '/'  => " ",
            '\'' => "' '",
            _    => "\"\""
        });
}
