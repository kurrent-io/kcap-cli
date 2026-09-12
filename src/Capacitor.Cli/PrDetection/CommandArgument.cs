using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.PrDetection;

/// <summary>
/// Gate for a value interpolated into a <see cref="CommandRunner"/> argument string, which the process
/// layer splits on whitespace and quotes: a git branch name may legally hold a quote, and a leading
/// '-' reads as an option.
/// </summary>
internal static partial class CommandArgument {
    public static bool IsPlain([NotNullWhen(true)] string? value) => value is not null && PlainRegex().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_./+@-]*\z")]
    private static partial Regex PlainRegex();
}
