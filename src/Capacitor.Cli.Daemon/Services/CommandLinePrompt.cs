using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Decides whether a launch prompt may stay on the command line. A command line is readable by every
/// local user through <c>ps</c> and matched by any <c>pkill -f</c> whose pattern appears in the prompt,
/// so a prompt is pasted into the agent's composer after spawn instead. Only a slash command stays
/// inline: the vendor runs it as a command when it is the literal first message. On Windows the
/// command line is also capped at 32,767 characters.
/// </summary>
internal static partial class CommandLinePrompt {
    /// <summary>Leaves room under the Windows limit for the resolved program, which for an npm shim is
    /// <c>node.exe</c> plus a script path.</summary>
    internal const int ArgumentBudget = 30_000;

    /// <summary>A slash command stays inline unless it is too long for a Windows command line, where
    /// the spawn would fail outright.</summary>
    internal static bool StaysInline(string prompt, IReadOnlyList<string> args) =>
        IsSlashCommand(prompt) && !(OperatingSystem.IsWindows() && Overflows(args));

    internal static bool Overflows(IReadOnlyList<string> args) {
        var length = 0;
        foreach (var arg in args) length += ConPtyProcess.QuoteArg(arg).Length + 1;
        return length > ArgumentBudget;
    }

    /// <summary>A leading absolute path is not a command, and a command is one line, so text on the
    /// lines after it is prose.</summary>
    internal static bool IsSlashCommand(string prompt) => SlashCommand().IsMatch(prompt);

    [GeneratedRegex(@"\A\s*/[A-Za-z0-9][A-Za-z0-9_.:-]*(?:[ \t][^\r\n]*)?\s*\z")]
    private static partial Regex SlashCommand();
}
