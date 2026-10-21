namespace Capacitor.App.Services;

public static class UninstallConfirmation {
    public static UninstallRequest Build(UninstallFacts facts) {
        var lines = new List<string> {
            "This removes kcap's hooks from your coding agents, every kcap background service, your kcap settings and sign-in, and then the kcap app itself.",
        };

        if (facts.Activity is { ActiveAgents: > 0 } active)
            lines.Add($"{active.ActiveAgents} running {(active.ActiveAgents == 1 ? "agent" : "agents")} will be ended.");
        else if (facts.Activity is { Busy: true })
            lines.Add("Work the kcap daemon is running will be ended.");
        else if (facts.Activity is null && facts.DaemonRunning)
            lines.Add("The kcap daemon is running but not answering. Anything it runs will be ended.");

        if (facts.HostedServerUrl is { } hosted)
            lines.Add($"Sessions already sent to {hosted} stay there.");

        foreach (var other in facts.OtherInstalls) {
            lines.Add(other.RemoveCommand is { } cmd
                ? $"Another kcap at {other.Path} will stop working. To remove it, run: {cmd}"
                : $"Another kcap at {other.Path} will stop working. Remove it yourself.");
        }

        var warning = facts.LocalServerUrl is { } local
            ? $"The local kcap server at {local} will be removed and every session recorded on it permanently deleted."
            : null;

        var prompt = new LifecyclePrompt(
            LifecyclePrompt.KindUninstall, null, null, false, string.Join("\n\n", lines), warning);
        IReadOnlyList<string> args = facts.LocalServerUrl is null ? [UninstallMode.Arg] : [UninstallMode.Arg, UninstallMode.PurgeLocalArg];
        return new UninstallRequest(prompt, args);
    }
}
