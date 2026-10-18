using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A launch prompt kept off the command line in an owner-only file the agent reads. A command line is
/// readable by every local user through <c>ps</c> and matched by any <c>pkill -f</c> whose pattern
/// appears in the prompt; on Windows it is also capped at 32,767 characters.
/// </summary>
internal static partial class PromptFile {
    /// <summary>Leaves room under the Windows limit for the resolved program, which for an npm shim is
    /// <c>node.exe</c> plus a script path.</summary>
    internal const int ArgumentBudget = 30_000;

    internal static bool Overflows(IReadOnlyList<string> args) {
        var length = 0;
        foreach (var arg in args) length += ConPtyProcess.QuoteArg(arg).Length + 1;
        return length > ArgumentBudget;
    }

    /// <summary>The agent runs a slash command only when it is the literal first message, so a pointer
    /// to a file would turn it into prose. A leading absolute path is not a command.</summary>
    internal static bool IsSlashCommand(string prompt) => SlashCommand().IsMatch(prompt);

    [GeneratedRegex(@"\A\s*/[A-Za-z0-9][A-Za-z0-9_.:-]*(\s|\z)")]
    private static partial Regex SlashCommand();

    internal static string Write(string root, string agentId, string prompt) {
        var dir = Path.Combine(root, AgentFileNames.For(agentId));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(dir);
        } else {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var path   = Path.Combine(dir, "prompt.md");
        var stream = new FileStream(path, options);
        try {
            using var writer = new StreamWriter(stream);
            writer.Write(prompt);
        } catch {
            Delete(path);
            throw;
        }
        return path;
    }

    internal static string Pointer(string path) =>
        $"Your instructions for this session are in the file {path}. Read the whole file first, then follow it as if it were this message.";

    internal static void Delete(string path) {
        try {
            if (Path.GetDirectoryName(path) is { } dir) Directory.Delete(dir, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes what a daemon that stopped without its exit continuations left behind. Its
    /// agents read their prompt as their first message, so none still needs one.</summary>
    internal static void Sweep(string root) {
        try {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
