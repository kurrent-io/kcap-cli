using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap refresh</c>: brings the agent integrations a previous kcap installed up to this version, after
/// an install or update. The one list every installer runs — the npm postinstall and launcher, the install
/// scripts and a script install's <c>kcap update</c> — so a new integration is added here once.
///
/// <para>Each step is a separate <c>kcap</c> run with its own deadline, so one that fails or stalls never
/// stops the others. Every step is a no-op unless the user opted in earlier: the plugin steps carry
/// <c>--if-installed</c>, and the daemon step acts only on service units that are already installed.</para>
/// </summary>
public sealed class RefreshCommand(IProcessRunner runner) {
    public RefreshCommand(TimeProvider time) : this(new ProcessRunner(time)) { }

    internal static readonly IReadOnlyList<string[]> Steps = [
        ["plugin", "install", "--skills",      "--if-installed"],
        ["plugin", "install", "--codex",       "--if-installed"],
        ["plugin", "install", "--cursor",      "--if-installed"],
        ["plugin", "install", "--copilot",     "--if-installed"],
        ["plugin", "install", "--pi",          "--if-installed"],
        ["plugin", "install", "--opencode",    "--if-installed"],
        ["plugin", "install", "--gemini",      "--if-installed"],
        ["plugin", "install", "--kiro",        "--if-installed"],
        ["plugin", "install", "--antigravity", "--if-installed"],
        ["plugin", "install",                  "--if-installed"],
        ["daemon", "service", "refresh"],
    ];

    /// <summary>Longer than the daemon step's own 55s deadline, so its last reload can finish.</summary>
    static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);

    public Task<int> HandleAsync(string[] args) =>
        RunAsync(Environment.ProcessPath, InstallProvenance.Kind(), () => SetupCommand.ResolvePluginPath(), Console.Out, Console.Error);

    /// <param name="binaryPath">The kcap each step runs: this process's own binary.</param>
    /// <param name="kind">Only an npm or script install owns its plugin directory; a dev checkout's is
    /// left alone.</param>
    internal async Task<int> RunAsync(
            string? binaryPath, InstallKind kind, Func<string?> resolvePluginDir, TextWriter stdout, TextWriter stderr) {
        if (string.IsNullOrEmpty(binaryPath)) {
            await stderr.WriteLineAsync("Cannot refresh: the kcap binary path is unknown.");
            return 1;
        }

        if (kind is InstallKind.Npm or InstallKind.Script && resolvePluginDir() is { } pluginDir) {
            var target = ScriptInstallLayout.Stabilize(binaryPath);
            if (PluginMcpConfigPatcher.Patch(pluginDir, target, out var error) == PluginMcpConfigPatcher.Outcome.Failed)
                await stderr.WriteLineAsync(
                    $"Could not point the Claude plugin's MCP entries at {target} ({error}); they keep launching `kcap` from PATH.");
        }

        var failed = new List<string>();

        foreach (var step in Steps) {
            ProcessResult result;
            try {
                result = await runner.RunAsync(binaryPath, [.. step],
                    new RunOptions(Timeout: StepTimeout), CancellationToken.None);
            } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) {
                failed.Add($"kcap {string.Join(' ', step)}: {ex.Message}");
                continue;
            }

            if (result.TimedOut)
                failed.Add($"kcap {string.Join(' ', step)}: timed out after {StepTimeout.TotalSeconds:0}s");
            else if (result.ExitCode != 0)
                failed.Add($"kcap {string.Join(' ', step)}: exit {result.ExitCode}{LastLine(result.Stderr)}");
        }

        if (failed.Count == 0) {
            await stdout.WriteLineAsync("Refreshed agent integrations.");
            return 0;
        }

        await stderr.WriteLineAsync("Some agent integrations were not refreshed:");
        foreach (var f in failed) await stderr.WriteLineAsync($"  {f}");

        return 1;
    }

    static string LastLine(string text) {
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last is null ? "" : $" ({last})";
    }
}
