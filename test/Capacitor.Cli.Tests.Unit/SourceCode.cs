using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Source text for a guard that matches code shapes, with every string literal's content blanked, so a literal
/// that spells the shape can neither pass for it nor hide its absence.</summary>
public static partial class SourceCode {
    [GeneratedRegex("\"\"\"[\\s\\S]*?\"\"\"|@\"(?:[^\"]|\"\")*\"|\"(?:[^\"\\\\\\n]|\\\\.)*\"")]
    private static partial Regex StringLiteral();

    public static string OutsideStrings(string code) => StringLiteral().Replace(code, "\"\"");
}
