using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.PrDetection;

/// <summary>
/// Gate for a value interpolated into a <see cref="CommandRunner"/> argument string. No shell is
/// involved, so only what the argument parser itself reads matters: whitespace splits the value in
/// two, a quote or backslash re-quotes the rest, and a leading '-' reads as an option. Everything
/// else a git ref or repository name may legally hold — '%', '=', a leading '.' as in an
/// organisation's <c>.github</c> — passes through as one argument.
/// </summary>
internal static partial class CommandArgument {
    public static bool IsPlain([NotNullWhen(true)] string? value) => value is not null && PlainRegex().IsMatch(value);

    [GeneratedRegex("""^(?!-)[^\s"'\\]+\z""")]
    private static partial Regex PlainRegex();
}
