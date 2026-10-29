using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public static class UninstallConfirmation {
    public static UninstallRequest Build(UninstallFacts facts) {
        var lines = new List<string> {
            "This removes kcap's hooks from your coding agents, every kcap background service, your kcap settings and sign-in, and then the kcap app itself.",
        };

        if (facts.PathProfiles is null)
            lines.Add("Any marked PATH line kcap added to your shell profile will also be removed.");
        else foreach (var profile in facts.PathProfiles)
            lines.Add($"The PATH line kcap added to the shell profile {profile} will be removed.");

        var attached = DaemonStore.Sanitize(facts.AttachedDaemon);

        if (facts.Activity is { ActiveAgents: > 0 } active)
            lines.Add($"{active.ActiveAgents} running {(active.ActiveAgents == 1 ? "agent" : "agents")} will be ended.");
        else if (facts.Activity is { Busy: true })
            lines.Add("Work the kcap daemon is running will be ended.");
        else if (facts.Activity is null && facts.LiveDaemons?.Contains(attached) == true)
            lines.Add("The kcap daemon is running but not answering. Anything it runs will be ended.");

        // The uninstall stops every daemon, not only the attached one.
        foreach (var other in facts.LiveDaemons?.Where(d => d != attached) ?? [])
            lines.Add($"The kcap daemon '{other}' is also running. Anything it runs will be ended.");

        if (facts.LiveDaemons is null)
            lines.Add(facts.Activity is null
                ? "Anything a running kcap daemon is doing will be ended."
                : "Anything another kcap daemon is running will be ended.");

        if (facts.HostedServerUrl is { } hosted)
            lines.Add($"Sessions already sent to {hosted} stay there.");

        if (facts.OtherInstalls is null)
            lines.Add("Any other kcap installed outside this app (npm, Homebrew, a script install) will also stop working.");

        foreach (var other in facts.OtherInstalls ?? []) {
            lines.Add(other.RemoveCommand is { } cmd
                ? $"Another kcap at {other.Path} will stop working. To remove it, run: {cmd}"
                : $"Another kcap at {other.Path} will stop working. Remove it yourself.");
        }

        var warning = facts.LocalServerUrl is { } local
            ? $"The local kcap server at {local} will be removed and every session recorded on it permanently deleted."
            : null;

        var prompt = new LifecyclePrompt(
            LifecyclePrompt.KindUninstall, null, null, false, string.Join("\n\n", lines), warning);
        return new UninstallRequest(prompt, facts.LocalServerUrl is not null);
    }
}
