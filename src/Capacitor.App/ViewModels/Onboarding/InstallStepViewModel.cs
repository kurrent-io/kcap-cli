using System.ComponentModel;
using System.Reactive;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalEdition;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// The local path's install page. The run belongs to <see cref="LocalInstallController"/>, which
/// outlives this page; the page renders its progress and settled outcome and advances by itself
/// once the outcome is Installed.
/// </summary>
public sealed class InstallStepViewModel : ReactiveObject, IWizardStep {
    public const string BusyText   = "Another kcap setup, upgrade or uninstall is running on this Mac.";
    public const string LockedText     = "The install did not finish.";
    public const string ProfileMismatchCode = "profile_mismatch";
    public const string AnswerRequiredText  = "This installer needs answers this version of kcap doesn't give. Update kcap, then try again.";
    public const string NotStartedText = "kcap didn't finish starting. Try again to finish the install.";
    public const string GaveUpText     = "kcap didn't finish starting, even after 10 tries. Email the logs to support and we'll help you get it running.";

    /// A start failure is retried this many times before the page shows it.
    public const int AutoRetries = 10;

    /// Lets a port or lock the failed start still held clear before the next attempt.
    static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(3);

    static readonly HashSet<string> StartFailureCodes = new(StringComparer.Ordinal) { "start_failed", "recovery_failed", "commit_failed" };
    public const string RepairingText  = "Updating an older kcap on this Mac";
    public const string OldClientText  = "An older kcap on this Mac is still in the way. Try again to update it.";

    readonly LocalInstallController _controller;
    readonly Func<string?>          _appProfile;
    readonly Func<string?>          _configActiveProfile;
    readonly Func<bool>             _onLocalPath;
    readonly Func<LocalEmbeddingSetting?> _recorded;
    readonly Func<CancellationToken, Task<LocalEmbeddingModelManifest?>> _builtinOffer;
    readonly Func<bool>             _resuming;
    readonly Func<CancellationToken, Task<ClientRepairOutcome>> _repairClients;
    readonly TimeProvider           _time;
    readonly Func<IReadOnlyList<string>, FeedbackMailOutcome>? _mailSupport;
    readonly List<string>           _startFailures = [];
    bool                            _autoRetrying;
    CancellationTokenSource?        _retryGap;
    string?                         _supportNote;
    CancellationToken               _lifetime;
    string?                         _refusal;
    string?                         _clientProblem;
    bool                            _repairing;
    bool                            _installPressed;
    bool                            _choosing;
    IReadOnlyList<InstallStepRow>   _rows = [];
    LocalSetupProgress?             _watched;

    /// <param name="builtinOffer">The built-in model set the stack setup will run offers, or null; asked before the choice shows.</param>
    /// <param name="recorded">The installation's recorded embedding setting; a disk read, run off the UI thread.</param>
    /// <param name="repairClients">Brings any other kcap the installer would refuse up to date, before anything is asked.</param>
    /// <param name="mailSupport">Opens a support email from what each failed start said; null offers none.</param>
    /// <param name="resuming">True while an earlier setup is unfinished: the page finishes it without asking again.</param>
    public InstallStepViewModel(
            LocalInstallController controller, Func<string?> appProfile, Func<string?> configActiveProfile,
            Func<bool> onLocalPath, Func<CancellationToken, Task<LocalEmbeddingModelManifest?>> builtinOffer,
            Func<LocalEmbeddingSetting?>? recorded = null,
            Func<CancellationToken, Task<ClientRepairOutcome>>? repairClients = null,
            TimeProvider? time = null,
            Func<IReadOnlyList<string>, FeedbackMailOutcome>? mailSupport = null,
            Func<bool>? resuming = null) {
        _controller          = controller;
        _appProfile          = appProfile;
        _configActiveProfile = configActiveProfile;
        _onLocalPath         = onLocalPath;
        _builtinOffer        = builtinOffer;
        _recorded            = recorded ?? (() => null);
        _resuming            = resuming ?? (() => false);
        _time                = time ?? TimeProvider.System;
        _mailSupport         = mailSupport;
        _repairClients       = repairClients ?? (_ => Task.FromResult<ClientRepairOutcome>(new ClientRepairOutcome.Clear()));

        Search          = new SearchChoiceViewModel();
        TryAgainCommand = ReactiveCommand.CreateFromTask(TryAgainAsync);
        RepairAgainCommand = ReactiveCommand.CreateFromTask(() =>
            !ClientProblemVisible ? Task.CompletedTask : _installPressed ? StartChosenAsync(_lifetime) : PrepareAsync(_lifetime));
        CancelCommand   = ReactiveCommand.Create(Cancel);
        EmailSupportCommand = ReactiveCommand.Create(EmailSupport);

        Search.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(SearchChoiceViewModel.Valid) or nameof(SearchChoiceViewModel.Checking))
                this.RaisePropertyChanged(nameof(CanContinue));
        };

        _controller.PropertyChanged += OnControllerChanged;
        _controller.Settled         += OnSettled;
        Rewire(_controller.Progress);
    }

    public WizardStepId Id                => WizardStepId.Install;
    /// Nothing installs while the search choice is up, so the headline says the page is still asking.
    public string       Title             => ChoiceVisible ? "One more step before installing" : "Installing kcap on this Mac";
    public string       Eyebrow           => "Install";
    public bool         Applicable        => true;
    public bool         Satisfied         => _controller.Outcome is LocalInstallOutcome.Installed;
    public bool         Skippable         => false;
    /// While the search choice is up, the footer's primary action starts the install.
    public bool         OwnsPrimaryAction => !ChoiceVisible;
    public string?      NextLabel         => ChoiceVisible ? "Install" : null;
    public bool         CanContinue       => !ChoiceVisible || (Search.Valid && !Search.Checking);
    public bool         PassThrough       => !_onLocalPath() || _controller.Outcome is LocalInstallOutcome.Installed;

    public string Lede => "kcap runs on this Mac and your sessions stay on it";

    public LocalSetupProgress Progress => _controller.Progress;

    public SearchChoiceViewModel Search { get; }

    /// The page asks about search by meaning before the run starts.
    public bool ChoiceVisible {
        get => _choosing;
        private set {
            this.RaiseAndSetIfChanged(ref _choosing, value);
            this.RaisePropertyChanged(nameof(Title));
            this.RaisePropertyChanged(nameof(ProgressVisible));
            this.RaisePropertyChanged(nameof(OwnsPrimaryAction));
            this.RaisePropertyChanged(nameof(NextLabel));
            this.RaisePropertyChanged(nameof(CanContinue));
        }
    }

    public IReadOnlyList<InstallStepRow> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public bool Running => _controller.Running;

    public bool DownloadBarVisible    => Progress.Download.State == RowState.Running;
    public bool DownloadIndeterminate => Progress.DownloadFraction is null;
    public double DownloadPercent     => (Progress.DownloadFraction ?? 0) * 100;

    LocalInstallOutcome.Failed? Failure => _controller.Outcome as LocalInstallOutcome.Failed;

    public bool FailureVisible => Failure is not null && !Running && !RefusalVisible && !AutoRetrying;

    /// Every automatic retry of a start failure has failed too.
    bool GaveUp => Failure is { Code: { } code } && StartFailureCodes.Contains(code) && _startFailures.Count > AutoRetries;

    public string? FailureMessage => Failure switch {
        null                                         => null,
        _ when GaveUp                                => GaveUpText,
        { Affordance: LocalInstallAffordance.Busy }  => BusyText,
        { Affordance: LocalInstallAffordance.Retry } => RetryGuidance(Failure.Code),
        { Code: "answer_required" }                  => AnswerRequiredText,
        var failed                                   => TerminalAdvice.Strip(failed.Message) ?? LockedText,
    };

    /// The CLI's own words behind a retryable failure, with its terminal commands dropped.
    public string? FailureDetail =>
        Failure is { Affordance: LocalInstallAffordance.Retry } failed ? TerminalAdvice.Strip(failed.Message) : null;

    public bool FailureDetailVisible => FailureDetail is not null && !Running;

    public bool LockedVisible   => Failure is { LockedToLocal: true } && !Running && !RefusalVisible;
    public bool TryAgainVisible => Failure is { Affordance: LocalInstallAffordance.Retry } && !Running && !GaveUp;
    public bool EmailSupportVisible => GaveUp && !Running && _mailSupport is not null;

    /// Between a failed start and the next attempt; the page shows progress, not the failure.
    public bool AutoRetrying {
        get => _autoRetrying;
        private set {
            this.RaiseAndSetIfChanged(ref _autoRetrying, value);
            this.RaisePropertyChanged(nameof(RetryStatus));
            RestateOutcome();
        }
    }

    public string? RetryStatus => AutoRetrying ? $"kcap didn't start, so setup is trying again ({_startFailures.Count} of {AutoRetries})" : null;

    /// What became of the support email: where it went, and where the full report is if the draft was cut.
    public string? SupportNote {
        get => _supportNote;
        private set => this.RaiseAndSetIfChanged(ref _supportNote, value);
    }
    public bool CancelVisible   => Running || AutoRetrying;
    public bool BackVisible     => !Running && !AutoRetrying && !Repairing && Failure is not { LockedToLocal: true };

    public bool ProgressVisible => RefusalMessage is null && !ChoiceVisible && !Repairing && !ClientProblemVisible;

    /// Another kcap is being brought up to date; nothing has been asked or installed yet.
    public bool Repairing {
        get => _repairing;
        private set {
            this.RaiseAndSetIfChanged(ref _repairing, value);
            this.RaisePropertyChanged(nameof(ProgressVisible));
            this.RaisePropertyChanged(nameof(BackVisible));
        }
    }

    /// Why another kcap could not be brought up to date; the page offers to try again.
    public string? ClientProblem {
        get => _clientProblem;
        private set {
            this.RaiseAndSetIfChanged(ref _clientProblem, value);
            this.RaisePropertyChanged(nameof(ClientProblemVisible));
            this.RaisePropertyChanged(nameof(ProgressVisible));
        }
    }

    public bool ClientProblemVisible => ClientProblem is not null;

    public string? RefusalMessage {
        get => _refusal;
        private set {
            this.RaiseAndSetIfChanged(ref _refusal, value);
            this.RaisePropertyChanged(nameof(RefusalVisible));
            this.RaisePropertyChanged(nameof(ProgressVisible));
            this.RaisePropertyChanged(nameof(FailureVisible));
            this.RaisePropertyChanged(nameof(LockedVisible));
        }
    }

    public bool RefusalVisible => RefusalMessage is not null;

    public ReactiveCommand<Unit, Unit> TryAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand   { get; }
    public ReactiveCommand<Unit, Unit> RepairAgainCommand { get; }
    public ReactiveCommand<Unit, Unit> EmailSupportCommand { get; }

    public event Action? AdvanceRequested;

    public async Task OnEnterAsync(CancellationToken ct) {
        _lifetime = ct;

        if (_controller.Outcome is LocalInstallOutcome.Installed) {
            AdvanceRequested?.Invoke();
            return;
        }

        // An older kcap may be gone by now, and refusing it changed nothing, so re-entry checks again.
        if (RefusedClient(_controller.Outcome)) _controller.Forget();
        // Any other settled failure stays on screen until Try again: re-entry never starts a run of its own.
        if (_controller.Running || _controller.Outcome is not null) return;

        // The installer writes the config's active profile; the app reads KCAP_PROFILE first.
        if (_appProfile() is { } app && _configActiveProfile() is { } active && !string.Equals(app, active, StringComparison.Ordinal)) {
            RefusalMessage =
                $"This app is using the profile \"{app}\" (from KCAP_PROFILE), but the install would set up \"{active}\", " +
                $"the active profile in your kcap config. Unset KCAP_PROFILE or make \"{app}\" the active profile, then reopen setup.";
            // Settled, not just shown: with nothing to retry, a close must not reopen this page.
            _controller.Refuse(ProfileMismatchCode, RefusalMessage);
            return;
        }

        RefusalMessage = null;
        if (ChoiceVisible || Repairing) return;

        await PrepareAsync(ct);
    }

    static bool RefusedClient(LocalInstallOutcome? outcome) =>
        outcome is LocalInstallOutcome.Failed { Code: var code } && code == LocalInstallFailureCode.ClientBelowFloor.Wire();

    /// Brings the other kcap copies up to date, then asks about search, or starts straight away when the answer is
    /// already known.
    async Task PrepareAsync(CancellationToken ct) {
        _installPressed = false;
        if (!await RepairAsync(ct)) return;

        LocalEmbeddingSetting? recorded;
        try {
            recorded = await Task.Run(_recorded, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        }
        Search.Prefill(recorded);
        // Only a known answer resumes unasked: the earlier run's choice may never have reached the record. A run
        // refused for an older kcap keeps the answer it was given.
        if ((_controller.Embeddings ?? (_resuming() ? Known(recorded) : null)) is { } known) {
            await _controller.StartAsync(known, ct);
            return;
        }

        LocalEmbeddingModelManifest? offered;
        try {
            offered = await _builtinOffer(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: looking up the built-in search model failed: {ex.Message}");
            offered = null;
        }
        Search.OfferBuiltin(offered);
        ChoiceVisible = true;
    }

    /// False, with the reason shown, when another kcap the installer would refuse is still there.
    async Task<bool> RepairAsync(CancellationToken ct) {
        ClientProblem = null;
        Repairing     = true;
        ClientRepairOutcome repair;
        try {
            repair = await _repairClients(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return false;
        } finally {
            Repairing = false;
        }
        if (!repair.Ready) ClientProblem = ProblemText(repair);

        return repair.Ready;
    }

    static string ProblemText(ClientRepairOutcome outcome) => outcome switch {
        ClientRepairOutcome.Cancelled c =>
            $"An older kcap at {c.Path} has to be updated before kcap can install, and macOS needs your password to do it. Try again to update it.",
        ClientRepairOutcome.OldApp a =>
            $"An older copy of the kcap app is at {a.Path}. Move it to the Trash, then try again.",
        ClientRepairOutcome.Failed f =>
            $"kcap couldn't update the older kcap at {f.Path}: {f.Reason} Try again to update it.",
        _ => OldClientText,
    };

    static LocalEmbeddingChoice? Known(LocalEmbeddingSetting? recorded) => recorded switch {
        null                                         => null,
        { Provider: LocalEmbeddingProvider.Builtin } => LocalEmbeddingChoice.Builtin,
        { Model: null }                              => LocalEmbeddingChoice.KeywordOnly,
        { Model: { } m }                             => new LocalEmbeddingChoice(m),
    };

    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        // Next on the choice is Install: the page stays, and advances itself once the run is installed.
        if (direction == WizardNavigation.Next && ChoiceVisible) {
            await InstallAsync();
            return false;
        }

        return direction switch {
            WizardNavigation.Back => BackVisible,
            WizardNavigation.Next => _controller.Outcome is LocalInstallOutcome.Installed,
            _                     => false,
        };
    }

    /// Stops following the controller, which outlives this page and the wizard that showed it.
    public void Detach() {
        _controller.PropertyChanged -= OnControllerChanged;
        _controller.Settled         -= OnSettled;
        if (_watched is not null) {
            _watched.PropertyChanged          -= OnProgressChanged;
            _watched.Download.PropertyChanged -= OnProgressChanged;
            _watched = null;
        }
    }

    static string RetryGuidance(string? code) => code switch {
        "download_failed"     => "The download didn't finish. Check your connection and try again.",
        "verification_failed" => "The download didn't pass its signature check. Try again to download it again.",
        "cancelled"           => "The install was cancelled.",
        "client_below_floor"  => OldClientText,
        null                  => "The install stopped before it finished. Try again.",
        _                     => NotStartedText,
    };

    Task InstallAsync() {
        if (!ChoiceVisible || !CanContinue) return Task.CompletedTask;

        ChoiceVisible = false;
        return StartChosenAsync(_lifetime);
    }

    /// Starts with the answer the user pressed Install on, once nothing older is in the way. Try again after a refusal
    /// here continues this same Install: an earlier run's answer, kept on the controller, was not what was chosen.
    async Task StartChosenAsync(CancellationToken ct) {
        _installPressed = true;
        // The choice can stay up across Back and a return to this page, long enough for an older kcap to turn up.
        if (!await RepairAsync(ct)) return;

        _installPressed = false;
        _startFailures.Clear();
        await _controller.StartAsync(Search.Choice, ct);
    }

    Task TryAgainAsync() {
        if (!TryAgainVisible) return Task.CompletedTask;
        _startFailures.Clear();
        if (!RefusedClient(_controller.Outcome)) return _controller.RetryAsync(_lifetime);

        _controller.Forget();
        return PrepareAsync(_lifetime);
    }

    void OnSettled(LocalInstallOutcome outcome) {
        if (outcome is LocalInstallOutcome.Failed { Code: { } code } failed && StartFailureCodes.Contains(code)) {
            _startFailures.Add(TerminalAdvice.Strip(failed.Message) ?? code);
            if (_startFailures.Count <= AutoRetries) {
                AutoRetrying = true;
                _retryGap?.Dispose();
                _retryGap = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                _ = RetryAfterGapAsync(_retryGap.Token);
                return;
            }
        }
        AutoRetrying = false;

        if (outcome is LocalInstallOutcome.Installed) AdvanceRequested?.Invoke();
        // Refused arguments changed nothing, so the answers can be given again.
        else if (outcome is LocalInstallOutcome.Failed { Code: "invalid_arguments" }) ChoiceVisible = true;
    }

    async Task RetryAfterGapAsync(CancellationToken ct) {
        try {
            await Task.Delay(RetryGap, _time, ct).ConfigureAwait(true);
            await _controller.RetryAsync(ct).ConfigureAwait(true);
        } catch (OperationCanceledException) {
            AutoRetrying = false;
        }
    }

    void Cancel() {
        if (!CancelVisible) return;
        if (Running) _controller.Interrupt();
        else _retryGap?.Cancel();
    }

    void EmailSupport() {
        if (!EmailSupportVisible) return;

        var sent = _mailSupport!(_startFailures);
        SupportNote = (sent.Opened, sent.Draft.CutShort, sent.Copied) switch {
            (true, true, true)   => $"Opened an email to {FeedbackMail.Address}. The logs were cut to fit it; the full report is on your clipboard, so paste it over the draft's text.",
            (true, _, _)         => $"Opened an email to {FeedbackMail.Address} in your mail app. Send it from there.",
            (false, _, true)     => $"Couldn't open a mail app. The report is on your clipboard: paste it into an email to {FeedbackMail.Address}.",
            (false, _, false)    => $"Couldn't open a mail app or copy the report. Email {FeedbackMail.Address} and we'll help.",
        };
    }

    void OnControllerChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(LocalInstallController.Progress):
                Rewire(_controller.Progress);
                this.RaisePropertyChanged(nameof(Progress));
                break;
            case nameof(LocalInstallController.Running) or nameof(LocalInstallController.Outcome):
                RestateOutcome();
                break;
        }
    }

    void RestateOutcome() {
        this.RaisePropertyChanged(nameof(Running));
        this.RaisePropertyChanged(nameof(Satisfied));
        this.RaisePropertyChanged(nameof(FailureVisible));
        this.RaisePropertyChanged(nameof(FailureMessage));
        this.RaisePropertyChanged(nameof(FailureDetail));
        this.RaisePropertyChanged(nameof(FailureDetailVisible));
        this.RaisePropertyChanged(nameof(LockedVisible));
        this.RaisePropertyChanged(nameof(TryAgainVisible));
        this.RaisePropertyChanged(nameof(EmailSupportVisible));
        this.RaisePropertyChanged(nameof(CancelVisible));
        this.RaisePropertyChanged(nameof(BackVisible));
    }

    void Rewire(LocalSetupProgress next) {
        if (_watched is not null) {
            _watched.PropertyChanged          -= OnProgressChanged;
            _watched.Download.PropertyChanged -= OnProgressChanged;
        }

        _watched = next;
        next.PropertyChanged          += OnProgressChanged;
        next.Download.PropertyChanged += OnProgressChanged;
        Rows = [
            new("Downloading kcap", next.Download),
            new("Verifying the signature", next.Verify),
            new("Installing kcap", next.Install),
            new("Search by meaning", next.Embeddings.Row),
            new("Starting kcap", next.FirstStart),
        ];
        RestateDownload();
    }

    void OnProgressChanged(object? sender, PropertyChangedEventArgs e) => RestateDownload();

    void RestateDownload() {
        this.RaisePropertyChanged(nameof(DownloadBarVisible));
        this.RaisePropertyChanged(nameof(DownloadIndeterminate));
        this.RaisePropertyChanged(nameof(DownloadPercent));
    }
}
