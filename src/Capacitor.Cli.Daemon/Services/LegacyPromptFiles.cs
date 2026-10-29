namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Earlier daemons wrote each launch prompt to <c>prompts/&lt;agent&gt;/prompt.md</c> under the state
/// directory and relied on their next start to sweep it, so one that crashed left whole prompts on
/// disk. Every start removes that directory, since nothing else will.
/// </summary>
internal static class LegacyPromptFiles {
    internal static void Sweep(string stateDir) {
        var root = Path.Combine(stateDir, "prompts");

        try {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
