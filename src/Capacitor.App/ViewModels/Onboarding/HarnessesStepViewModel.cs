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
/// <c>kcap plugin install</c> per selected row in registry order; a failed row stays with its own
/// Retry and the page waits for a second Next rather than moving on as if it had worked.
/// </summary>
public sealed class HarnessesStepViewModel : ReactiveObject, IWizardStep {
    /// The four answers the CLI offers, narrowest first, over <see cref="AppConfig.ValidVisibilities"/>.
    public static readonly IReadOnlyList<VisibilityOption> VisibilityOptions = [
        new("private", "Only me", "Nobody else can read them."),
        new("project", "People I share a project with", "Readable by other members of your projects that hold the repository."),
        new("org_public", "Everyone in the account, for your organization's repositories",
            "Sessions in a repository your organization owns are readable account-wide. Your other repositories stay private unless a project you share covers them."),
        new("public", "Everyone in the account", "Readable by every member of this Capacitor account."),
    ];

    internal static readonly IReadOnlyList<string> ProviderKeys = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY"];

    readonly IKcapCli                                                          _cli;
    readonly Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> _detect;
    readonly Func<IReadOnlySet<HarnessId>>                                      _declined;
    readonly Func<CancellationToken, Task<IReadOnlySet<string>?>>               _providerKeys;
    readonly ConfigRoot                                                         _config;
    readonly Func<string?>?                                                     _resolveProfileName;
    readonly Action<IEnumerable<HarnessId>>                                     _stampOffered;

    bool    _detected;
    bool    _installed;
    bool    _busy;
    bool    _satisfied;
    string  _visibility = "org_public";
    bool    _useProviderApiKey;
    string? _providerKeyNotice;
    string? _message;
    Task?   _inFlight;

    public HarnessesStepViewModel(
            IKcapCli cli,
            Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect,
            Func<IReadOnlySet<HarnessId>> declined,
            Action<IEnumerable<HarnessId>> stampOffered,
            ConfigRoot config,
            PathFixViewModel? pathFix,
            Func<CancellationToken, Task<IReadOnlySet<string>?>> providerKeys,
            string machineName,
            Func<string?>? resolveProfileName = null) {
        _cli                = cli;
        _detect             = detect;
        _declined           = declined;
        _stampOffered       = stampOffered;
        _config             = config;
        _providerKeys       = providerKeys;
        _resolveProfileName = resolveProfileName;
        PathFix             = pathFix;
        MachineName         = machineName;

        if (pathFix is not null)
            pathFix.WhenAnyValue(x => x.Fixed).Subscribe(_ => this.RaisePropertyChanged(nameof(PathHazard)));
    }

    public WizardStepId Id         => WizardStepId.Harnesses;
    public string       Title      => "Connect Capacitor to your harnesses";
    public string       Eyebrow    => "Your harnesses";
    public bool         Applicable => true;
    public string       SkipLabel  => "Not now";

    public string Lede =>
        "Sessions record themselves from now on, and each harness gets tools to search your past work — " +
        "so what you already fixed is there to find.";

    public string MachineName { get; }

    public IReadOnlyList<HarnessRowViewModel> Rows { get; private set; } = [];

    /// "Not found: …" under the rows, or null when every harness was found.
    public string? NotFoundLine { get; private set; }

    public bool NoneFound => _detected && Rows.Count == 0;

    public PathFixViewModel? PathFix { get; }

    /// The login shell cannot find kcap, and nothing has fixed that yet.
    public bool PathHazard => PathFix is { Fixed: false };

    public bool PathResolved => PathFix is { Fixed: true };

    public bool CliAvailable => _cli.CliPath is not null;

    public string Visibility {
        get => _visibility;
        set => this.RaiseAndSetIfChanged(ref _visibility, value);
    }

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
        }
    }

    public bool Idle => !Busy;

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

    public string? NextLabel =>
        _installed                 ? "Continue"
        : !CliAvailable            ? "Continue"
        : SelectedCount == 0       ? "Turn on"
        : SelectedCount == 1       ? "Turn on for 1 harness"
        :                            $"Turn on for {SelectedCount} harnesses";

    public bool DeclineVisible => SelectedCount == 0 && Rows.Count > 0;

    public async Task OnEnterAsync(CancellationToken ct) {
        if (_detected) return; // re-entering must not stomp the user's choices

        IReadOnlyDictionary<HarnessId, DetectedAgent> detected;
        try { detected = await _detect(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }

        var declined = _declined();
        var idle     = this.WhenAnyValue(x => x.Busy, busy => !busy);
        Rows = [.. AgentVendors.All
            .Where(v => detected.ContainsKey(v.Id))
            .Select(v => new HarnessRowViewModel(v, detected[v.Id], declined.Contains(v.Id), RetryOneAsync, idle))];

        foreach (var row in Rows) {
            row.WhenAnyValue(x => x.Selected, x => x.Record).Subscribe(_ => RestateSelection());
        }

        var missing = AgentVendors.All.Where(v => !detected.ContainsKey(v.Id)).Select(v => v.Label).ToList();
        NotFoundLine = missing.Count == 0 ? null : "Not found: " + string.Join(", ", missing);
        _detected    = true;

        this.RaisePropertyChanged(nameof(Rows));
        this.RaisePropertyChanged(nameof(NotFoundLine));
        this.RaisePropertyChanged(nameof(NoneFound));
        RestateSelection();

        await ReadProviderKeysAsync(ct).ConfigureAwait(true);
    }

    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (_inFlight is { } run) {
            try { await run.ConfigureAwait(true); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard harness install failed unexpectedly: {ex.Message}"); }
        }

        if (direction == WizardNavigation.Back) return true;

        _stampOffered(Rows.Select(r => r.Id));
        if (direction == WizardNavigation.Skip) return true;

        if (!await PersistAsync(ct).ConfigureAwait(true)) return false;
        if (_installed || !CliAvailable) return true;

        await (_inFlight = InstallSelectedAsync()).ConfigureAwait(true);
        _installed = true;
        this.RaisePropertyChanged(nameof(NextLabel));

        // A failure is shown on its row with Retry; the next Next is the user's "carry on anyway".
        return Rows.Where(r => r.Selected).All(r => r.Succeeded);
    }

    async Task<bool> PersistAsync(CancellationToken ct) {
        try {
            await ConfigMutator.MutateAsync(_config, c => {
                var resolvedName = _resolveProfileName?.Invoke();
                var activeName   = resolvedName is not null && c.Profiles.ContainsKey(resolvedName)
                    ? resolvedName
                    : string.IsNullOrWhiteSpace(c.ActiveProfile) ? "default" : c.ActiveProfile;
                var profile = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

                profile = profile with {
                    DefaultVisibility = Visibility,
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
            foreach (var row in Rows.Where(r => r.Selected).ToList()) await InstallOneAsync(row).ConfigureAwait(true);
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
        try { await InstallOneAsync(row).ConfigureAwait(true); }
        finally { Busy = false; }
    }

    async Task InstallOneAsync(HarnessRowViewModel row) {
        row.Status  = AgentInstallStatus.Installing;
        row.Message = null;

        try {
            var result = await _cli.PluginInstallAsync(row.Flag, CancellationToken.None, row.InstallOptions).ConfigureAwait(true);
            if (result.ExitCode == 0) {
                row.Status = AgentInstallStatus.Succeeded;
            } else {
                row.Status  = AgentInstallStatus.Failed;
                row.Message = string.IsNullOrWhiteSpace(result.Stderr) ? $"Install failed (exit {result.ExitCode})." : result.Stderr.Trim();
            }
        } catch (Exception ex) {
            row.Status  = AgentInstallStatus.Failed;
            row.Message = ex.Message;
        }

        var selected = Rows.Where(r => r.Selected).ToList();
        Satisfied = selected.Count > 0 && selected.All(r => r.Succeeded);
    }

    async Task ReadProviderKeysAsync(CancellationToken ct) {
        IReadOnlySet<string>? set;
        try { set = await _providerKeys(ct).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) {
            Console.Error.WriteLine($"kcap: provider key probe failed: {ex.Message}");
            return;
        }

        var anthropic = set?.Contains("ANTHROPIC_API_KEY") == true && Rows.Any(r => r.Id == HarnessId.Claude);
        var openai    = set?.Contains("OPENAI_API_KEY") == true && Rows.Any(r => r.Id == HarnessId.Codex);

        ProviderKeyNotice = (anthropic, openai) switch {
            (true, true)  => "ANTHROPIC_API_KEY and OPENAI_API_KEY are set in your terminal.",
            (true, false) => "ANTHROPIC_API_KEY is set in your terminal.",
            (false, true) => "OPENAI_API_KEY is set in your terminal.",
            _             => null,
        };
    }

    void RestateSelection() {
        this.RaisePropertyChanged(nameof(SelectedCount));
        this.RaisePropertyChanged(nameof(NextLabel));
        this.RaisePropertyChanged(nameof(DeclineVisible));
    }

    /// The login-shell PATH when the probe resolves one, in place of the process's own: a GUI
    /// launch inherits only launchd's PATH, and the app spawns kcap with the terminal's — so
    /// detecting through the wider process PATH would report agents its own installs cannot reach.
    public static Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> BuildDetectionFeed(
            ILoginShellProbe probe, UserHome home) {
        // Resolved once, outside the closure: every step that asks shares one feed.
        var harnesses = HarnessRegistry.FromEnvironment(home);

        return async ct => {
            var terminalPath = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
            var searched     = terminalPath is null ? harnesses : harnesses.Searching(BinaryProbe.Searching(terminalPath));

            // A snapshot rather than the live registry: rows are built once and must not move under
            // the user while they are choosing.
            return searched.Select(h => (h.Id, Detected: searched.Detect(h.Id)))
                .Where(x => x.Detected.Detected)
                .ToDictionary(x => x.Id, x => x.Detected);
        };
    }
}
