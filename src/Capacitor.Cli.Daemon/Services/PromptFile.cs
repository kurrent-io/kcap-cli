using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A launch prompt kept off the command line in an owner-only file the agent reads. A command line is
/// readable by every local user through <c>ps</c> and matched by any <c>pkill -f</c> whose pattern
/// appears in the prompt; on Windows it is also capped at 32,767 characters.
/// </summary>
internal static class PromptFile {
    /// <summary>Leaves room under the Windows limit for the resolved program, which for an npm shim is
    /// <c>node.exe</c> plus a script path.</summary>
    internal const int ArgumentBudget = 30_000;

    internal static bool Overflows(IReadOnlyList<string> args) {
        var length = 0;
        foreach (var arg in args) length += ConPtyProcess.QuoteArg(arg).Length + 1;
        return length > ArgumentBudget;
    }

    /// <summary>The agent runs a slash command only when it is the literal first message, so a pointer
    /// to a file would turn it into prose.</summary>
    internal static bool IsSlashCommand(string prompt) => prompt.AsSpan().TrimStart().StartsWith("/");

    internal static string Write(string root, string agentId, string prompt) {
        var dir = Path.Combine(root, agentId);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(dir);
        } else {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var path = Path.Combine(dir, "prompt.md");
        using (var writer = new StreamWriter(new FileStream(path, options))) writer.Write(prompt);
        return path;
    }

    internal static string Pointer(string path) =>
        $"Your instructions for this session are in the file {path}. Read the whole file first, then follow it as if it were this message.";

    internal static void Delete(string path) {
        try {
            if (Path.GetDirectoryName(path) is { } dir) Directory.Delete(dir, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
