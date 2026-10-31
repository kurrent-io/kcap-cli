using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// Which harnesses record and get the tools, who can read what they record, and — when the login
/// shell cannot find kcap — the PATH fix the hooks depend on. Next writes the profile, then runs one
/// <c>kcap plugin install</c> per selected row in registry order. The page then stays, so each row's
/// outcome and the terminal note are read where they happened; a second Next moves on.
/// </summary>
public sealed class HarnessesStepViewModel : ReactiveObject, IWizardStep {
    /// Every answer, narrowest first, offered when the server does not say which this tenant can honour
    public static readonly IReadOnlyList<VisibilityOption> AllVisibilityOptions = [
        new("private", "Only me", "Nobody else can read them."),
        new("project", "People I share a project with", "Readable by other members of your projects that hold the repository."),
        new("org_public", "Everyone in the account, for your organization's repositories",
            "Sessions in a repository your organization owns are readable account-wide. Your other repositories stay private unless a project you share covers them."),
        new("public", "Everyone in the account", "Readable by every member of this kcap account."),
    ];

    internal static readonly IReadOnlyList<string> ProviderKeys = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY"];

    readonly IKcapCli _cli;
    readonly Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> _detect;
    readonly Func<IReadOnlySet<HarnessId>> _declined;
    readonly Func<CancellationToken, Task<IReadOnlySet<string>?>> _providerKeys;
    readonly ConfigRoot _config;
    readonly Func<string?>? _resolveProfileName;
    readonly Action<IEnumerable<HarnessId>> _stampOffered;
    readonly CancellationToken _shutdownToken;
    readonly Func<bool>? _onLocalPath;
    readonly Func<CancellationToken, Task<VisibilityOptionsResponse?>>? _visibilityOptions;

    bool _detected;
    bool _installed;
    bool _busy;
    bool _satisfied;
    string? _visibility;
    bool _useProviderApiKey;
    string? _providerKeyNotice;
    string? _message;
    Task? _inFlight;

    public HarnessesStepViewModel(
            IKcapCli cli,
            Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect,
            Func<IReadOnlySet<HarnessId>> declined,
            Action<IEnumerable<HarnessId>> stampOffered,
            ConfigRoot config,
            PathFixViewModel? pathFix,
            Func<CancellationToken, Task<IReadOnlySet<string>?>> providerKeys,
            string machineName,
            Func<string?>? resolveProfileName = null, CancellationToken shutdownToken = default,
            Func<bool>? onLocalPath = null,
            Func<CancellationToken, Task<VisibilityOptionsResponse?>>? visibilityOptions = null) {
        _onLocalPath = onLocalPath;
        _visibilityOptions = visibilityOptions;
        _cli = cli;
        _detect = detect;
        _declined = declined;
        _stampOffered = stampOffered;
        _config = config;
        _providerKeys = providerKeys;
        _resolveProfileName = resolveProfileName;
        _shutdownToken = shutdownToken;
        PathFix = pathFix;
        MachineName = machineName;

        if (pathFix is not null)
        {
            pathFix.WhenAnyValue(x => x.Fixed).Subscribe(_ => {
                this.RaisePropertyChanged(nameof(PathHazard));
                this.RaisePropertyChanged(nameof(PathResolved));
            });
            pathFix.WhenAnyValue(x => x.Plan).Subscribe(_ => this.RaisePropertyChanged(nameof(PathNote)));
        }
    }

    const string PrivacyRequiredMessage = "Choose who can read new sessions before continuing.";

    public WizardStepId Id => WizardStepId.Harnesses;
    public string Title => "Connect kcap to your harnesses";
    public string Eyebrow => "Your harnesses";
    public bool Applicable => true;
    public string SkipLabel => "Not now";

    /// Once harnesses are on, Not now has nothing left to decline.
    public bool Skippable => !_installed;

    public string Lede =>
        "Sessions are stored from now on, and each harness gets tools to search your past work";

    public string MachineName { get; }

    public IReadOnlyList<HarnessRowViewModel> Rows { get; private set; } = [];

    /// "Not found: …" under the rows, or null when every harness was found.
    public string? NotFoundLine { get; private set; }

    /// Why some rows start off, or null when every row was found by a command or an app.
    public string? SettingsOnlyNote { get; private set; }

    public bool NoneFound => _detected && Rows.Count == 0;
    /// Detection in flight — table chrome must wait; Busy alone also covers install.
    public bool IsDetecting => Busy && !_detected;
    public bool ShowsTable => _detected && Rows.Count > 0;

    public PathFixViewModel? PathFix { get; }

    /// The login shell cannot find kcap, and nothing has fixed that yet.
    public bool PathHazard => PathFix is { Fixed: false };

    public bool PathResolved => PathFix is { Fixed: true };

    /// Not now installs nothing, so the PATH change is offered rather than announced.
    public IReadOnlyList<DisclosureSpan> PathNote =>
        [new("Harness hooks run "), new("kcap", Code: true),
         new(" from your terminal, so turning a harness on adds it there. Not now leaves your terminal as it is. "),
         .. PathFix?.Plan ?? []];

    /// A terminal reads PATH when it starts, so one already open may still not find kcap.
    public IReadOnlyList<DisclosureSpan> PathResolvedNote =>
        [new("Terminals you open from now on can run "), new("kcap", Code: true), new(", so harnesses started from them record.")];

    public bool CliAvailable => _cli.CliPath is not null;

    /// The answers this tenant can honour, in the server's order and copy, or every answer when it does not say
    public IReadOnlyList<VisibilityOption> VisibilityOptions { get; private set; } = AllVisibilityOptions;

    public string? Visibility {
        get => _visibility;
        set {
            this.RaiseAndSetIfChanged(ref _visibility, value);
            this.RaisePropertyChanged(nameof(CanContinue));
            this.RaisePropertyChanged(nameof(CanSkip));
            if (HasVisibilityChoice && Message == PrivacyRequiredMessage) Message = null;
        }
    }

    bool Local => _onLocalPath?.Invoke() == true;

    public string SavedNote => SetupLocation.DisconnectLaterNote;

    public string CliMissingNote => Local
        ? "The kcap command line isn't available on this machine, so there is nothing to turn on from here."
        : "kcap isn't available on this machine, so there is nothing to turn on from here.";

    public string ProviderKeyDetail => Local
        ? " By default kcap leaves them out when it runs Claude or Codex for titles and summaries, so your subscription is used. Keep them if you pay per use."
        : " By default kcap leaves them out when it runs Claude or Codex for titles and summaries, so your subscription is used. Keep them if you pay per use.";

    /// A local install keeps sessions on this machine, so there is no audience to choose.
    public bool VisibilityVisible => _onLocalPath?.Invoke() != true;

    bool HasVisibilityChoice => Visibility is { } value && AppConfig.ValidVisibilities.Contains(value);

    /// Set when the terminal exports a provider key a recording harness would otherwise have kcap scrub.
    public string? ProviderKeyNotice {
        get => _providerKeyNotice;
        private set => this.RaiseAndSetIfChanged(ref _providerKeyNotice, value);
    }

    public bool UseProviderApiKey {
        get => _useProviderApiKey;
        set => this.RaiseAndSetIfChanged(ref _useProviderApiKey, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
            this.RaisePropertyChanged(nameof(ChoicesEditable));
            this.RaisePropertyChanged(nameof(CanContinue));
            this.RaisePropertyChanged(nameof(CanSkip));
            this.RaisePropertyChanged(nameof(IsDetecting));
        }
    }

    public bool Idle => !Busy;
    public bool ChoicesEditable => Idle && !_installed;
    public bool ConnectionsSaved => _installed;
    public bool CanContinue => _detected && Idle && HasVisibilityChoice;
    public bool CanSkip => CanContinue;

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    /// Why the page did not move on: a missing CLI, or a profile that could not be saved.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public int SelectedCount => Rows.Count(r => r.Selected);

    public IReadOnlyList<HarnessId> Recording => [.. Rows.Where(r => r.Record && r.Succeeded).Select(r => r.Id)];

    public string? NextLabel =>
        _installed ? "Continue"
        : !CliAvailable ? "Continue"
        : SelectedCount == 0 ? "Continue"
        : SelectedCount == 1 ? "Turn on for 1 harness"
        : $"Turn on for {SelectedCount} harnesses";

    public bool DeclineVisible => SelectedCount == 0 && Rows.Count > 0;

    public async Task OnEnterAsync(CancellationToken ct) {
        // Re-entering must not stomp the user's choices, but the offer may have changed since.
        if (_detected) {
            if (VisibilityVisible && _visibilityOptions is not null) {
                Busy = true;
                try { await OfferAsync(_visibilityOptions(ct)).ConfigureAwait(true); } finally { Busy = false; }
            }
            return;
        }
        if (_onLocalPath?.Invoke() == true) Visibility = "private";
        this.RaisePropertyChanged(nameof(VisibilityVisible));
        Busy = true;
        try {
            var offered = VisibilityVisible ? _visibilityOptions?.Invoke(ct) : null;

            IReadOnlyDictionary<HarnessId, DetectedAgent> detected;
            try { detected = await _detect(ct).ConfigureAwait(true); } catch (OperationCanceledException) { return; } catch (Exception ex) {
                Message = $"Could not check your harnesses: {ex.Message}. Go back and try again, or continue setup later.";
                return;
            }

            try {
                var config = await AppConfig.LoadProfileConfig(_config, ct).ConfigureAwait(true);
                var profileName = _resolveProfileName?.Invoke() ?? config.ActiveProfile;
                if (config.Profiles.GetValueOrDefault(profileName ?? ProfileConfig.DefaultName) is { } profile)
                    UseProviderApiKey = profile.UseProviderApiKey;
            } catch (Exception ex) {
                Message = $"Could not read your settings: {ex.Message}. Go back and try again.";
                return;
            }

            if (offered is not null) await OfferAsync(offered).ConfigureAwait(true);

            var declined = _declined();
            var idle = this.WhenAnyValue(x => x.Busy, busy => !busy);
            Rows = [.. AgentVendors.All
            .Where(v => detected.ContainsKey(v.Id))
            .Select(v => new HarnessRowViewModel(v, detected[v.Id], declined.Contains(v.Id), RetryOneAsync, idle))];

            foreach (var row in Rows) {
                row.WhenAnyValue(x => x.Selected, x => x.Record).Subscribe(_ => RestateSelection());
            }

            var missing = AgentVendors.All.Where(v => !detected.ContainsKey(v.Id)).Select(v => v.Label).ToList();
            NotFoundLine = missing.Count == 0 ? null : "Not found: " + string.Join(", ", missing);
            SettingsOnlyNote = SettingsOnlyNoteFor([.. Rows.Where(r => r.SettingsOnly && !r.Declined).Select(r => r.Label)]);
            _detected = true;

            this.RaisePropertyChanged(nameof(Rows));
            this.RaisePropertyChanged(nameof(NotFoundLine));
            this.RaisePropertyChanged(nameof(SettingsOnlyNote));
            this.RaisePropertyChanged(nameof(NoneFound));
            this.RaisePropertyChanged(nameof(IsDetecting));
            this.RaisePropertyChanged(nameof(ShowsTable));
            RestateSelection();

            await ReadProviderKeysAsync(ct).ConfigureAwait(true);
        } finally {
            Busy = false;
        }
    }

    /// A failed read keeps every answer on screen: the step must stay answerable whatever the server does
    async Task OfferAsync(Task<VisibilityOptionsResponse?> offered) {
        VisibilityOptionsResponse? offer;
        try { offer = await offered.ConfigureAwait(true); } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: visibility options read failed — offering every answer: {ex.Message}");
            offer = null;
        }

        VisibilityOptions = offer is null
            ? AllVisibilityOptions
            : [.. offer.Options.Select(o => new VisibilityOption(o.Key!, o.Label!, o.Detail ?? ""))];
        this.RaisePropertyChanged(nameof(VisibilityOptions));
        if (VisibilityOptions.All(o => o.Value != Visibility)) Visibility = null;
    }

    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (_inFlight is { } run) {
            try { await run.ConfigureAwait(true); } catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard harness install failed unexpectedly: {ex.Message}"); }
        }

        if (direction == WizardNavigation.Back) return true;

        if (!_detected || Busy) return false;
        if (!HasVisibilityChoice) {
            Message = PrivacyRequiredMessage;
            return false;
        }

        if (!await PersistAsync(ct).ConfigureAwait(true)) return false;

        // Hooks store the bare command, so turning a harness on first puts kcap on the login
        // shell. Not now installs nothing, so it does not wait.
        if (direction != WizardNavigation.Skip && PathFix is { Fixed: false } pathFix && Rows.Any(r => r.Selected)) {
            Message = null;
            Busy    = true;
            bool installed;
            try { installed = await pathFix.InstallAsync(ct).ConfigureAwait(true); } finally { Busy = false; }
            if (!installed) {
                Message = pathFix.Message;
                return false;
            }
        }

        _stampOffered(Rows.Select(r => r.Id));
        if (direction == WizardNavigation.Skip) return true;
        if (_installed || !CliAvailable) return true;

        await (_inFlight = InstallSelectedAsync()).ConfigureAwait(true);
        _installed = true;
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(ChoicesEditable));
        this.RaisePropertyChanged(nameof(ConnectionsSaved));
        this.RaisePropertyChanged(nameof(Skippable));

        // Nothing selected installed nothing, so there is no outcome to stay for.
        return !Rows.Any(r => r.Selected);
    }

    async Task<bool> PersistAsync(CancellationToken ct) {
        if (Visibility is not { } visibility || !AppConfig.ValidVisibilities.Contains(visibility)) return false;
        try {
            await ConfigMutator.MutateAsync(_config, c => {
                var resolvedName = _resolveProfileName?.Invoke();
                var activeName = resolvedName is not null && c.Profiles.ContainsKey(resolvedName)
                    ? resolvedName
                    : string.IsNullOrWhiteSpace(c.ActiveProfile) ? "default" : c.ActiveProfile;
                var profile = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

                profile = profile with {
                    DefaultVisibility = visibility,
                    // Only a question that was asked is answered: no key in the terminal leaves any
                    // earlier opt-in alone.
                    UseProviderApiKey = ProviderKeyNotice is null ? profile.UseProviderApiKey : UseProviderApiKey,
                };

                return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = profile } };
            }, ct).ConfigureAwait(true);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Message = $"We could not save that: {ex.Message}";

            return false;
        }

        Message = null;

        return true;
    }

    async Task InstallSelectedAsync() {
        Busy = true;
        try {
            foreach (var row in Rows.Where(r => r.Selected).ToList()) {
                _shutdownToken.ThrowIfCancellationRequested();
                await InstallOneAsync(row).ConfigureAwait(true);
            }
        } finally {
            Busy = false;
        }
    }

    internal Task RetryOneAsync(HarnessRowViewModel row) {
        if (Busy) return Task.CompletedTask;

        return _inFlight = RetryCoreAsync(row);
    }

    async Task RetryCoreAsync(HarnessRowViewModel row) {
        Busy = true;
        try { await InstallOneAsync(row).ConfigureAwait(true); } finally { Busy = false; }
    }

    async Task InstallOneAsync(HarnessRowViewModel row) {
        row.Status = AgentInstallStatus.Installing;
        row.Message = null;

        try {
            var result = await _cli.PluginInstallAsync(row.Flag, _shutdownToken, row.InstallOptions).ConfigureAwait(true);
            if (result.ExitCode == 0 && !result.TimedOut) {
                row.Status = AgentInstallStatus.Succeeded;
            } else {
                row.Status = AgentInstallStatus.Failed;
                row.Message = string.IsNullOrWhiteSpace(result.Stderr) ? $"Install failed (exit {result.ExitCode})." : result.Stderr.Trim();
            }
        } catch (Exception ex) {
            row.Status = AgentInstallStatus.Failed;
            row.Message = ex.Message;
        }

        var selected = Rows.Where(r => r.Selected).ToList();
        Satisfied = selected.Count > 0 && selected.All(r => r.Succeeded);
    }

    async Task ReadProviderKeysAsync(CancellationToken ct) {
        IReadOnlySet<string>? set;
        try { set = await _providerKeys(ct).ConfigureAwait(true); } catch (OperationCanceledException) { return; } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: provider key probe failed: {ex.Message}");
            return;
        }

        var anthropic = set?.Contains("ANTHROPIC_API_KEY") == true && Rows.Any(r => r.Id == HarnessId.Claude);
        var openai = set?.Contains("OPENAI_API_KEY") == true && Rows.Any(r => r.Id == HarnessId.Codex);

        ProviderKeyNotice = (anthropic, openai) switch {
            (true, true) => "ANTHROPIC_API_KEY and OPENAI_API_KEY are set in your terminal.",
            (true, false) => "ANTHROPIC_API_KEY is set in your terminal.",
            (false, true) => "OPENAI_API_KEY is set in your terminal.",
            _ => null,
        };
    }

    internal static string? SettingsOnlyNoteFor(IReadOnlyList<string> labels) => labels switch {
        [] => null,
        [var one] => $"Only {one}'s settings folder was found, so it starts off. Turn it on if you use it.",
        _ => $"Only the settings folders of {string.Join(", ", labels.SkipLast(1))} and {labels[^1]} were found, " +
             "so they start off. Turn on the ones you use.",
    };

    void RestateSelection() {
        this.RaisePropertyChanged(nameof(SelectedCount));
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(DeclineVisible));
    }

    /// Searches the login-shell PATH when the probe resolves one, in place of <paramref name="harnesses"/>'
    /// own: a GUI launch inherits only launchd's PATH, and the app spawns kcap with the terminal's — so
    /// detecting through the process PATH would report agents its own installs cannot reach.
    public static Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> BuildDetectionFeed(
            ILoginShellProbe probe, HarnessRegistry harnesses) =>
        async ct => {
            var terminalPath = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
            var searched = terminalPath is null ? harnesses : harnesses.Searching(BinaryProbe.Searching(terminalPath));

            // A snapshot rather than the live registry: rows are built once and must not move under
            // the user while they are choosing.
            return searched.Select(h => (h.Id, Detected: searched.Detect(h.Id)))
                .Where(x => x.Detected.Detected)
                .ToDictionary(x => x.Id, x => x.Detected);
        };
}
