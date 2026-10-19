using System.Globalization;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>The install of <c>kcap setup --local</c> in a terminal: the preflight, the two answers, then the install
/// rendered as status lines.</summary>
/// <param name="report">The whole run's JSON report, which takes every install event and failure as well.</param>
sealed class LocalSetupTerminal(ILocalInstallerFactory factory, LocalSetupArgs args, SetupJsonReport? report = null) {
    public const string SingleUserNotice =
        "This first version is for a machine with one user: on a shared machine, other users can open the web UI and act as its admin.";

    public const string EmbeddingModelNotice =
        "The embedding model fixes the width of the stored vectors, so changing it later means embedding everything again.";

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
            return await FailAsync(LocalSetupJsonLines.Classify(ex));
        }

        _version = plan.Version;

        var root   = installer.RootDirectory;
        var canAsk = !args.NoPrompt && AnsiConsole.Profile.Capabilities.Interactive;

        AnsiConsole.MarkupLine($"  {Markup.Escape(SingleUserNotice)}");

        if (!canAsk && MissingAnswer(args, plan, root) is { } missing) return await RefuseAsync(missing);

        if (!args.SingleUser && !Ask("Continue?")) return Declined();

        var confirmed = args.Yes;

        // A reuse asks nothing, and passes on no confirmation: a plan that becomes an install under the lease
        // must still be refused for want of one.
        if (!confirmed && plan.Hint != PlanHint.Reuse) {
            if (!Ask($"Install Capacitor local {plan.Version} in {root}?")) return Declined();

            confirmed = true;
        }

        LocalEmbeddingAnswer embeddings;

        try {
            if (await EmbeddingsAsync(installer, canAsk) is not { } answer)
                return await RefuseAsync(MissingEmbeddings(installer.Ollama!.BaseUrl));

            embeddings = answer;
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex));
        }

        // Taken after the questions, so a Ctrl+C at a prompt still exits at once: nothing exists yet to roll back.
        using var deferral = InteractiveLifetime.Defer();

        try {
            var committed = await installer.InstallAsync(new(SingleUser: true, Confirmed: confirmed, embeddings), progress, deferral.Token);

            return deferral.Interrupted ? (SetupExit.Interrupted, null) : (SetupExit.Done, committed);
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex));
        }
    }

    /// <summary>The flags' answer; else what an earlier setup recorded, or the step's own report when Ollama is not
    /// running; else the user's. Null when only the user could answer and cannot be asked.</summary>
    async Task<LocalEmbeddingAnswer?> EmbeddingsAsync(LocalInstaller installer, bool canAsk) {
        var recorded   = installer.EmbeddingSetting;
        var configured = recorded?.Model;

        if (args.Embeddings is { } given) {
            if (configured is not null && given is LocalEmbeddingAnswer.Ollama { Model: { } model } && model != configured)
                AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape($"{model} replaces {configured}. {EmbeddingModelNotice}")}");

            return given;
        }

        if (recorded is not null) return new LocalEmbeddingAnswer.Ollama(null);

        var ollama = installer.Ollama ?? throw new InvalidOperationException("The installer has no Ollama client.");

        if (await ollama.VersionAsync(CancellationToken.None) is null) return new LocalEmbeddingAnswer.Ollama(null);

        if (!canAsk) return null;

        AnsiConsole.MarkupLine($"  {Markup.Escape($"Ollama is running on {Url(ollama.BaseUrl)}, so search can find sessions and memories by meaning as well as by keyword.")}");
        AnsiConsole.MarkupLine($"  {Markup.Escape(EmbeddingModelNotice)}");

        if (!Ask("Use Ollama for semantic search?", defaultValue: true)) return new LocalEmbeddingAnswer.Skip();

        var chosen = AnsiConsole.Prompt(new TextPrompt<string>("Embedding model:")
            .DefaultValue(OllamaClient.DefaultModel)
            .Validate(m => LocalSetupArgs.IsModelName(m) ? ValidationResult.Success() : ValidationResult.Error("Not an Ollama model name.")));

        return new LocalEmbeddingAnswer.Ollama(chosen);
    }

    static string MissingEmbeddings(Uri ollama) =>
        $"Ollama is running on {Url(ollama)}. Pass {LocalSetupArgs.EmbeddingModelFlag} {OllamaClient.DefaultModel} to use it for semantic search, "
      + $"or {LocalSetupArgs.NoEmbeddingsFlag} to search by keyword. {EmbeddingModelNotice}";

    static string Url(Uri uri) => uri.ToString().TrimEnd('/');

    static bool Ask(string question, bool defaultValue = false) =>
        AnsiConsole.Prompt(new ConfirmationPrompt(Markup.Escape(question)) { DefaultValue = defaultValue });

    static (int, LocalInstallEvent.Committed?) Declined() {
        AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

        return (0, null);
    }

    Task<(int, LocalInstallEvent.Committed?)> RefuseAsync(string message) =>
        FailAsync(new LocalInstallFailure(LocalInstallFailureCode.AnswerRequired, message));

    async Task<(int, LocalInstallEvent.Committed?)> FailAsync(LocalInstallFailure failure) {
        await Console.Error.WriteLineAsync($"  Error: {failure.Message}");
        report?.Failed(failure.Code.Wire(), failure.Message);

        return (LocalSetupJsonLines.Exit(failure.Code), null);
    }

    void Render(LocalInstallEvent e) {
        report?.Install(e);

        if (Line(e, _version) is { } line) AnsiConsole.MarkupLine($"  {Markup.Escape(line)}");

        if (e is LocalInstallEvent.Reused reused && reused.InstalledVersion != _version)
            AnsiConsole.MarkupLine($"  [dim]This kcap is {Markup.Escape(_version)}; run kcap local upgrade to move the stack to it.[/]");
    }

    /// <summary>The status line for an event, or null for one that has none. The <c>kcap local</c> commands render a
    /// recovery with it too.</summary>
    public static string? Line(LocalInstallEvent e, string version) =>
        e switch {
            LocalInstallEvent.PreflightPassed { Plan: PlanHint.Reuse } p => $"Reusing the local installation in {p.Root}",
            LocalInstallEvent.PreflightPassed p => $"Installing Capacitor local {p.Version} in {p.Root}",
            LocalInstallEvent.Recovering r      => $"Finishing an interrupted {r.Operation} (stopped at {r.Phase})…",
            LocalInstallEvent.Recovered r       => $"Finished the interrupted {r.Operation}",
            LocalInstallEvent.PortChosen p      => $"Using port {p.Port}",
            LocalInstallEvent.Downloading d     => $"Downloading Capacitor local {version} ({Megabytes(d.Bytes)} MB)…",
            LocalInstallEvent.Verified          => "Verified",
            LocalInstallEvent.Unpacked u        => $"Unpacked into {u.Path}",
            LocalInstallEvent.Starting          => "Starting the local server…",
            LocalInstallEvent.Ready r           => $"Ready on {r.ServerUrl}",
            LocalInstallEvent.Committing        => "Committing…",
            LocalInstallEvent.Reused r          => $"Reusing Capacitor local {r.InstalledVersion} on {r.ServerUrl}",
            LocalInstallEvent.RollingBack r     => $"Rolling back: {r.Reason}",
            LocalInstallEvent.RolledBack        => "Rolled back",
            LocalInstallEvent.Committed c       => $"Committed: Capacitor local {c.Version} on {c.ServerUrl}",
            LocalInstallEvent.UpgradePlanned u  => $"Upgrading Capacitor local {u.From} {UpdateCommand.Arrow} {u.To}",
            LocalInstallEvent.Stopping          => "Stopping the local server…",
            LocalInstallEvent.Stopped           => "Stopped",
            LocalInstallEvent.Switched s        => $"Switched to {s.Version}",
            LocalInstallEvent.Restarting r      => $"Starting Capacitor local {r.Version} again…",
            LocalInstallEvent.Upgraded u        => $"Upgraded Capacitor local {u.From} {UpdateCommand.Arrow} {u.To} on {u.ServerUrl}",
            LocalInstallEvent.NothingToDo n     => n.Reason switch {
                UpgradeSkip.Current    => $"Capacitor local is already at {n.Version}.",
                UpgradeSkip.StackNewer => $"Capacitor local {n.InstalledVersion} is newer than this kcap ({n.Version}); it is left as it is, and this kcap keeps working with it.",
                _                      => "There is no local installation to upgrade.",
            },
            LocalInstallEvent.EmbeddingOllamaFound o   => $"Found Ollama {o.Version} on {o.Url}",
            LocalInstallEvent.EmbeddingModelPulling p  => $"Pulling {p.Model} into Ollama…",
            LocalInstallEvent.EmbeddingModelProgress p => Tenth(p.Received, p.Bytes) is { } tenth ? $"  {tenth}% of {Megabytes(p.Bytes)} MB" : null,
            LocalInstallEvent.EmbeddingModelReady r    => $"{r.Model} is ready ({r.Dimensions} dimensions)",
            LocalInstallEvent.EmbeddingsConfigured { Replaced: { } old } c =>
                $"Semantic search uses {c.Model} through Ollama; everything embedded with {old} is embedded again in the background",
            LocalInstallEvent.EmbeddingsConfigured c   => $"Semantic search uses {c.Model} through Ollama",
            LocalInstallEvent.EmbeddingsSkipped k      => k.Message,
            _                                   => null,
        };

    /// <summary>The percentage when it is a multiple of ten, so a terminal shows a pull in ten lines.</summary>
    static long? Tenth(long received, long bytes) => bytes > 0 && received * 100 / bytes % 10 == 0 ? received * 100 / bytes : null;

    static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);
}
