using System.Globalization;
using Capacitor.Cli.LocalEdition;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>The install of <c>kcap setup --local</c> in a terminal: the preflight, the two answers, then the install
/// rendered as status lines.</summary>
sealed class LocalSetupTerminal(ILocalInstallerFactory factory, LocalSetupArgs args) {
    public const string SingleUserNotice =
        "This first version is for a machine with one user: on a shared machine, other users can open the web UI and act as its admin.";

    string _version = "";

    /// <summary>The first answer a run that cannot ask is missing, or null when it has both. A reuse needs no
    /// confirmation: it installs nothing.</summary>
    public static string? MissingAnswer(LocalSetupArgs args, LocalPlan plan, string root) =>
        !args.SingleUser ? MissingSingleUser
      : !args.Yes && plan.Hint != PlanHint.Reuse ? MissingYes(plan.Version, root)
      : null;

    const string MissingSingleUser =
        "Pass --single-user to confirm this machine has one user: on a shared machine, other users can open the web UI and act as its admin.";

    static string MissingYes(string version, string root) => $"Pass --yes to install Capacitor local {version} in {root}.";

    /// <returns>The commit, or null with the exit code when the later steps must not run.</returns>
    public async Task<(int Exit, LocalInstallEvent.Committed? Committed)> RunAsync() {
        var progress = new SyncProgress<LocalInstallEvent>(Render);

        LocalInstaller installer;
        LocalPlan      plan;

        try {
            installer = factory.Create();
            plan      = installer.Preflight();
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex).Message);
        }

        _version = plan.Version;

        var root   = installer.RootDirectory;
        var canAsk = !args.NoPrompt && AnsiConsole.Profile.Capabilities.Interactive;

        AnsiConsole.MarkupLine($"  {Markup.Escape(SingleUserNotice)}");

        if (!canAsk && MissingAnswer(args, plan, root) is { } missing) return await FailAsync(missing);

        if (!args.SingleUser && !Ask("Continue?")) return Declined();

        var confirmed = args.Yes;

        // A reuse asks nothing, and passes on no confirmation: a plan that becomes an install under the lease
        // must still be refused for want of one.
        if (!confirmed && plan.Hint != PlanHint.Reuse) {
            if (!Ask($"Install Capacitor local {plan.Version} in {root}?")) return Declined();

            confirmed = true;
        }

        // Taken after the questions, so a Ctrl+C at a prompt still exits at once: nothing exists yet to roll back.
        using var deferral = InteractiveLifetime.Defer();

        try {
            var committed = await installer.InstallAsync(new(SingleUser: true, Confirmed: confirmed), progress, deferral.Token);

            return deferral.Interrupted ? (130, null) : (0, committed);
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex).Message);
        }
    }

    static bool Ask(string question) =>
        AnsiConsole.Prompt(new ConfirmationPrompt(Markup.Escape(question)) { DefaultValue = false });

    static (int, LocalInstallEvent.Committed?) Declined() {
        AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

        return (0, null);
    }

    static async Task<(int, LocalInstallEvent.Committed?)> FailAsync(string message) {
        await Console.Error.WriteLineAsync($"  Error: {message}");

        return (1, null);
    }

    void Render(LocalInstallEvent e) {
        var line = e switch {
            LocalInstallEvent.PreflightPassed { Plan: PlanHint.Reuse } p => $"Reusing the local installation in {p.Root}",
            LocalInstallEvent.PreflightPassed p => $"Installing Capacitor local {p.Version} in {p.Root}",
            LocalInstallEvent.Recovering r      => $"Finishing an interrupted {r.Operation} (stopped at {r.Phase})…",
            LocalInstallEvent.Recovered r       => $"Finished the interrupted {r.Operation}",
            LocalInstallEvent.PortChosen p      => $"Using port {p.Port}",
            LocalInstallEvent.Downloading d     => $"Downloading Capacitor local {_version} ({Megabytes(d.Bytes)} MB)…",
            LocalInstallEvent.Verified          => "Verified",
            LocalInstallEvent.Unpacked u        => $"Unpacked into {u.Path}",
            LocalInstallEvent.Starting          => "Starting the local server…",
            LocalInstallEvent.Ready r           => $"Ready on {r.ServerUrl}",
            LocalInstallEvent.Committing        => "Committing…",
            LocalInstallEvent.Reused r          => $"Reusing Capacitor local {r.InstalledVersion} on {r.ServerUrl}",
            LocalInstallEvent.RollingBack r     => $"Rolling back: {r.Reason}",
            LocalInstallEvent.RolledBack        => "Rolled back",
            LocalInstallEvent.Committed c       => $"Committed: Capacitor local {c.Version} on {c.ServerUrl}",
            _                                   => null,
        };

        if (line is not null) AnsiConsole.MarkupLine($"  {Markup.Escape(line)}");

        if (e is LocalInstallEvent.Reused reused && reused.InstalledVersion != _version)
            AnsiConsole.MarkupLine($"  [dim]This kcap is {Markup.Escape(_version)}; upgrades arrive with kcap local upgrade.[/]");
    }

    static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);
}
