using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli;

/// <summary>
/// kcap's config hook (git 2.54+) in the global git config, which an older git ignores. Leaves
/// <c>hook.kcap.enabled</c>, the user's off switch, as it finds it.
/// </summary>
sealed class GitHookInstaller(UserHome home, Func<string?>? resolveBinaryPath = null) {
    // Not reference-transaction: when that hook fails, git refuses the commit. post-commit also fires
    // for amend, rebase and cherry-pick, and a merge commit fires only post-merge.
    static readonly string[] Events = ["post-commit", "post-merge"];

    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    const string Section = $"hook.{GitHook.Name}";

    public bool Install() => Entry() == Expected() || Write();

    /// <summary>
    /// Repoints an entry already there at this binary, and adds none.
    /// </summary>
    public bool Refresh() => Entry() is { } entry && (entry == Expected() || Write());

    public void Remove() => Git("--remove-section", Section);

    /// <summary>
    /// The hook's command for <paramref name="binary"/>. git runs it through sh: single-quoted, with
    /// forward slashes for Git for Windows.
    /// </summary>
    public static string CommandFor(string binary) =>
        $"'{binary.Replace('\\', '/').Replace("'", "'\\''")}' git-hook";

    // The native binary rather than the npm launcher on PATH, which adds half a second to every commit
    // on the machine.
    string Command() => CommandFor(KcapBinaryCommand.Resolve(resolveBinaryPath));

    string Expected() =>
        string.Join('\n', [$"{Section}.command {Command()}", ..Events.Select(hookEvent => $"{Section}.event {hookEvent}")]);

    string? Entry() => Git("--get-regexp", $@"^hook\.{GitHook.Name}\.(command|event)$");

    bool Write() {
        const string events = $"{Section}.event";

        // Fails on a first install, when there is nothing to unset yet.
        Git("--unset-all", events);

        return Git($"{Section}.command", Command()) is not null
            && Events.All(hookEvent => Git("--add", events, hookEvent) is not null);
    }

    // The file `git config --global` would write: ~/.gitconfig, unless only the XDG file exists.
    string ConfigFile() {
        var dotfile = Path.Combine(home.Path, ".gitconfig");
        var xdg     = Path.Combine(home.Path, ".config", "git", "config");

        return !File.Exists(dotfile) && File.Exists(xdg) ? xdg : dotfile;
    }

    // By name, since --global follows $HOME rather than the home kcap was given. Under GIT_CONFIG_GLOBAL
    // git never reads it, and then no watcher relies on the hook.
    string? Git(params string[] arguments) {
        try {
            var start = new ProcessStartInfo("git") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };

            foreach (var argument in (string[])["config", "--file", ConfigFile(), ..arguments])
                start.ArgumentList.Add(argument);

            using var git = Process.Start(start);

            if (git is null) return null;

            if (!git.WaitForExit(GitTimeout)) {
                git.Kill();

                return null;
            }

            return git.ExitCode == 0 ? git.StandardOutput.ReadToEnd().TrimEnd() : null;
        } catch {
            return null;
        }
    }
}
