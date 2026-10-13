using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// A launch prompt moved off the command line into a file the agent reads. Windows caps a command line
/// at 32,767 characters, so a prompt that embeds a whole diff makes <c>CreateProcess</c> fail with error
/// 206 before the agent starts.
/// </summary>
internal static class PromptFile {
    /// <summary>Leaves room under the limit for the resolved program, which for an npm shim is
    /// <c>node.exe</c> plus a script path.</summary>
    internal const int ArgumentBudget = 30_000;

    internal static string DefaultRoot => Path.Combine(Path.GetTempPath(), "kcap-prompts");

    internal static bool Overflows(IReadOnlyList<string> args) {
        var length = 0;
        foreach (var arg in args) length += ConPtyProcess.QuoteArg(arg).Length + 1;
        return length > ArgumentBudget;
    }

    internal static string Write(string root, string agentId, string prompt) {
        var dir = Path.Combine(root, agentId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "prompt.md");
        File.WriteAllText(path, prompt);
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
