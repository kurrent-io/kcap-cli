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

    /// <summary>Every answer a run that cannot ask is missing, or null when it has both, so one retry is
    /// enough. A reuse needs no confirmation: it installs nothing.</summary>
    public static string? MissingAnswer(LocalSetupArgs args, LocalPlan plan, string root) {
        var needsYes = !args.Yes && plan.Hint != PlanHint.Reuse;

        return !args.SingleUser && needsYes ? MissingBoth(plan.Version, root)
             : !args.SingleUser ? MissingSingleUser
             : needsYes ? MissingYes(plan.Version, root)
             : null;
    }

    const string MissingSingleUser =
        "Pass --single-user to confirm this machine has one user: on a shared machine, other users can open the web UI and act as its admin.";

    static string MissingBoth(string version, string root) =>
        $"Pass --single-user and --yes to install kcap local {version} in {root}. --single-user confirms this machine has one user: on a shared machine, other users can open the web UI and act as its admin.";

    static string MissingYes(string version, string root) => $"Pass --yes to install kcap local {version} in {root}.";

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
            if (!Ask($"Install kcap local {plan.Version} in {root}?")) return Declined();

            confirmed = true;
        }

        LocalEmbeddingAnswer         embeddings;
        LocalEmbeddingModelManifest? builtin;

        try {
            builtin    = await BuiltinModelAsync(installer, plan, args.Embeddings, CancellationToken.None);
            embeddings = await EmbeddingsAsync(installer, builtin, canAsk);
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex));
        }

        // Taken after the questions, so a Ctrl+C at a prompt still exits at once: nothing exists yet to roll back.
        using var deferral = InteractiveLifetime.Defer();

        try {
            var committed = await installer.InstallAsync(new(SingleUser: true, Confirmed: confirmed, embeddings, builtin), progress, deferral.Token);

            if (!deferral.Interrupted) return (SetupExit.Done, committed);

            // The process exits as the deferral is disposed, before the caller could close the report.
            report?.Failed(SetupFailureCodes.Cancelled, "Interrupted after the local install committed; run kcap setup again to finish the other steps.");

            return (SetupExit.Interrupted, null);
        } catch (Exception ex) {
            return await FailAsync(LocalSetupJsonLines.Classify(ex));
        }
    }

    /// <summary>The built-in model set the version this setup leaves running offers, looked up only for an answer that
    /// may use it. Null when there is none.</summary>
    public static async Task<LocalEmbeddingModelManifest?> BuiltinModelAsync(LocalInstaller installer, LocalPlan plan, LocalEmbeddingAnswer answer,
        CancellationToken ct) {
        if (answer is not (LocalEmbeddingAnswer.Default or LocalEmbeddingAnswer.Builtin)) return null;
        if (answer is LocalEmbeddingAnswer.Default && installer.EmbeddingSetting is { Provider: not LocalEmbeddingProvider.Builtin }) return null;

        try {
            return await installer.FetchEmbeddingManifestAsync(plan.Existing?.Version ?? plan.Version, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return null; // The install that follows reports the cancellation.
        }
    }

    /// <summary>The flags' answer; else, when an earlier setup recorded a choice or nobody can be asked, the default;
    /// else the user's.</summary>
    async Task<LocalEmbeddingAnswer> EmbeddingsAsync(LocalInstaller installer, LocalEmbeddingModelManifest? builtin, bool canAsk) {
        var recorded   = installer.EmbeddingSetting;
        var configured = recorded?.Model;
        var given      = args.Embeddings;

        if (given is not LocalEmbeddingAnswer.Default) {
            if (configured is not null && given is LocalEmbeddingAnswer.Ollama { Model: { } model } && model != configured)
                AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape($"{model} replaces {configured}. {EmbeddingModelNotice}")}");

            return given;
        }

        if (recorded is not null || !canAsk) return given;

        var ollama  = installer.Ollama ?? throw new InvalidOperationException("The installer has no Ollama client.");
        var running = await ollama.VersionAsync(CancellationToken.None) is not null;

        if (!running && builtin is null) return given;

        const string keyword = "Keyword search only";
        var withOllama  = $"Ollama on {Url(ollama.BaseUrl)}";
        var withBuiltin = builtin is null ? null : $"The built-in model ({Megabytes(builtin.TotalSize)} MB download)";

        AnsiConsole.MarkupLine($"  {Markup.Escape("Search can find sessions and memories by meaning as well as by keyword.")}");
        AnsiConsole.MarkupLine($"  {Markup.Escape(EmbeddingModelNotice)}");

        var choices = new List<string>();
        if (running) choices.Add(withOllama);
        if (withBuiltin is not null) choices.Add(withBuiltin);
        choices.Add(keyword);

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Search by meaning with:").UseConverter(Markup.Escape).AddChoices(choices));

        if (picked == keyword) return new LocalEmbeddingAnswer.Skip();
        if (picked == withBuiltin) return new LocalEmbeddingAnswer.Builtin();

        var chosen = AnsiConsole.Prompt(new TextPrompt<string>("Embedding model:")
            .DefaultValue(OllamaClient.DefaultModel)
            .Validate(m => LocalEmbeddingModelName.IsValid(m) ? ValidationResult.Success() : ValidationResult.Error("Not an Ollama model name.")));

        return new LocalEmbeddingAnswer.Ollama(chosen);
    }

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
            LocalInstallEvent.PreflightPassed p => $"Installing kcap local {p.Version} in {p.Root}",
            LocalInstallEvent.Recovering r      => $"Finishing an interrupted {r.Operation} (stopped at {r.Phase})…",
            LocalInstallEvent.Recovered r       => $"Finished the interrupted {r.Operation}",
            LocalInstallEvent.PortChosen p      => $"Using port {p.Port}",
            LocalInstallEvent.Downloading d     => $"Downloading kcap local {version} ({Megabytes(d.Bytes)} MB)…",
            LocalInstallEvent.Verified          => "Verified",
            LocalInstallEvent.Unpacked u        => $"Unpacked into {u.Path}",
            LocalInstallEvent.Starting          => "Starting the local server…",
            LocalInstallEvent.Ready r           => $"Ready on {r.ServerUrl}",
            LocalInstallEvent.Committing        => "Committing…",
            LocalInstallEvent.Reused r          => $"Reusing kcap local {r.InstalledVersion} on {r.ServerUrl}",
            LocalInstallEvent.RollingBack r     => $"Rolling back: {r.Reason}",
            LocalInstallEvent.RolledBack        => "Rolled back",
            LocalInstallEvent.Committed c       => $"Committed: kcap local {c.Version} on {c.ServerUrl}",
            LocalInstallEvent.UpgradePlanned u  => $"Upgrading kcap local {u.From} {UpdateCommand.Arrow} {u.To}",
            LocalInstallEvent.Stopping          => "Stopping the local server…",
            LocalInstallEvent.Stopped           => "Stopped",
            LocalInstallEvent.Switched s        => $"Switched to {s.Version}",
            LocalInstallEvent.Restarting r      => $"Starting kcap local {r.Version} again…",
            LocalInstallEvent.Upgraded u        => $"Upgraded kcap local {u.From} {UpdateCommand.Arrow} {u.To} on {u.ServerUrl}",
            LocalInstallEvent.NothingToDo n     => n.Reason switch {
                UpgradeSkip.Current    => $"kcap local is already at {n.Version}.",
                UpgradeSkip.StackNewer => $"kcap local {n.InstalledVersion} is newer than this kcap ({n.Version}); it is left as it is, and this kcap keeps working with it.",
                _                      => "There is no local installation to upgrade.",
            },
            LocalInstallEvent.EmbeddingOllamaFound o   => $"Found Ollama {o.Version} on {o.Url}",
            LocalInstallEvent.EmbeddingModelPulling p  => $"Pulling {p.Model} into Ollama…",
            LocalInstallEvent.EmbeddingModelProgress p => Tenth(p.Received, p.Bytes) is { } tenth ? $"  {tenth}% of {Megabytes(p.Bytes)} MB" : null,
            LocalInstallEvent.EmbeddingModelReady r    => $"{r.Model} is ready ({r.Dimensions} dimensions)",
            LocalInstallEvent.EmbeddingsConfigured { Replaced: { } old } c =>
                $"Semantic search uses {c.Model} through Ollama; everything embedded with {old} is embedded again in the background",
            LocalInstallEvent.EmbeddingsConfigured c   => $"Semantic search uses {c.Model} through Ollama",
            LocalInstallEvent.EmbeddingBuiltinDownloading d => $"Downloading the built-in embedding model ({Megabytes(d.Bytes)} MB)…",
            LocalInstallEvent.EmbeddingBuiltinProgress p    => Tenth(p.Received, p.Bytes) is { } tenth ? $"  {tenth}% of {Megabytes(p.Bytes)} MB" : null,
            LocalInstallEvent.EmbeddingBuiltinConfigured { Replaced: { } old } =>
                $"Semantic search uses the built-in model; everything embedded with {old} is embedded again in the background",
            LocalInstallEvent.EmbeddingBuiltinConfigured    => "Semantic search uses the built-in model",
            LocalInstallEvent.EmbeddingsSkipped k      => k.Message,
            _                                   => null,
        };

    /// <summary>The percentage when it is a multiple of ten, so a terminal shows a pull in ten lines.</summary>
    static long? Tenth(long received, long bytes) => bytes > 0 && received * 100 / bytes % 10 == 0 ? received * 100 / bytes : null;

    static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture);
}
