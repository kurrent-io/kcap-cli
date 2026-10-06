using System.Collections.ObjectModel;
using System.Reactive;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// <summary>
/// Runs ONE façade operation for the staged intent and renders its structured progress: notices,
/// the browser fallback URL, the device code, the tenant list, and the create-workspace prompts.
/// Nothing starts on entry — the step has an explicit Sign in action, because the operation is the
/// only thing on the wizard that reaches the network. Cancellation is never rendered as a failure.
/// </summary>
public sealed class SignInStepViewModel : ReactiveObject, IWizardStep {
    internal const int LogLimit = 200;

    /// How long the re-auth dialog keeps the success line up before it closes.
    internal static readonly TimeSpan SuccessHold = TimeSpan.FromMilliseconds(1600);

    /// One pending UI answer. The flow parks on <see cref="AskAsync"/>; the view resolves it, a
    /// cancel releases it, and the prompt is torn down either way.
    sealed class UiQuestion<T> {
        TaskCompletionSource<T>? _pending;

        public async Task<T> AskAsync(Action<Action> post, Action show, Action hide, CancellationToken ct) {
            var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pending, pending);

            using var registration = ct.Register(() => pending.TrySetCanceled(ct));

            post(show);

            try {
                return await pending.Task.ConfigureAwait(false);
            } finally {
                Interlocked.CompareExchange(ref _pending, null, pending);
                post(hide);
            }
        }

        public void Answer(T value) => Interlocked.Exchange(ref _pending, null)?.TrySetResult(value);
    }

    readonly WizardAuthService       _service;
    readonly ConnectChoiceViewModel  _connect;
    readonly WizardTenantPicker      _picker;
    readonly ConsentFlipClaims       _claims;
    readonly IAppStateStore          _appState;
    readonly IUrlOpener              _urlOpener;
    readonly Action<Action>          _post;
    readonly string?                 _committedDetail;
    readonly TimeProvider            _time;
    readonly Func<Func<string, CancellationToken, Task<AvailabilityResponse?>>?> _provisionerCheck;

    readonly UiQuestion<ProvisionMode> _mode    = new();
    readonly UiQuestion<string?>       _orgName = new();
    readonly UiQuestion<string?>       _slug    = new();
    readonly UiQuestion<bool>          _confirm = new();

    AuthAttempt?   _attempt;
    ConnectIntent? _running;
    Task?          _run;
    string?        _lastReport;

    string  _status = "";
    bool    _statusIsError;
    string? _statusDetail;
    bool    _busy;
    bool    _satisfied;
    string? _deviceCode;
    string? _verificationUri;
    string? _browserUrl;
    string? _waitingText;
    string? _quarantineNotice;
    bool    _tenantPickerVisible;
    bool    _modeChoiceVisible;
    bool    _orgNamePromptVisible;
    bool    _slugPromptVisible;
    bool    _confirmVisible;
    string  _orgNameText = "";
    string  _slugText = "";
    string? _slugError;
    string  _existingWorkspaceInput = "";
    string  _confirmText = "";
    string? _provisioningProgress;
    bool    _urlPanelVisible;
    bool    _statusIsReady;
    bool    _slugEdited;
    bool    _settingDerivedSlug;
    bool    _orgNameAnswered;
    string? _slugHint;
    bool    _slugAvailable;

    DiscoveredTenant? _selectedTenant;

    public SignInStepViewModel(
            WizardAuthService    service,
            ConnectChoiceViewModel connect,
            WizardBridges        bridges,
            ConsentFlipClaims    claims,
            IAppStateStore       appState,
            IUrlOpener           urlOpener,
            // Shown under the success headline. The re-auth dialog supplies its own; the wizard does not.
            string?              committedDetail = null,
            TimeProvider?        time = null) {
        _time            = time ?? TimeProvider.System;
        _service         = service;
        _connect         = connect;
        _picker          = bridges.Picker;
        _claims          = claims;
        _appState        = appState;
        _urlOpener       = urlOpener;
        _post            = bridges.Post;
        _committedDetail = committedDetail;

        bridges.Progress.NoticeReceived += line => {
            // Kept as the failure detail's fallback: a decline is reported as a notice, not an error.
            _lastReport = line;
            Append(line);
        };
        bridges.Progress.ErrorReceived += line => {
            // Headline + StatusDetail only — Append would double the line in the progress log.
            var detail = FormatErrorDetail(line);
            _lastReport  = detail;
            StatusDetail = detail;
        };
        bridges.Progress.BrowserOpened      += url => {
            BrowserUrl = url;
            // StatusDetail survives here: SetStatus only clears it on the next non-error headline.
            StatusDetail = "Finish authorization in the browser, then return here. This window updates when you're done.";
            WaitingText  = "Waiting for you to authorize…";
            Append("Opened the sign-in page in your browser.");
        };
        bridges.Progress.DeviceCodeReceived += (code, verificationUri, prefilled) => {
            DeviceCode      = StripClipboardNote(code);
            VerificationUri = verificationUri;
            // Raw here, stripped above: the chip is what the user reads, the log records what was
            // actually reported - including the clipboard note. Pinned by the view-model tests.
            Append(prefilled
                ? $"Check the code shown is {code} at {verificationUri}"
                : $"Enter the code {code} at {verificationUri}");
        };
        bridges.Progress.PollTicked += () => WaitingText = "Waiting for you to authorize…";

        _picker.SelectionRequested += tenants => _post(() => {
            Tenants.Clear();
            foreach (var tenant in tenants) Tenants.Add(tenant);
            SelectedTenant      = Tenants.FirstOrDefault();
            TenantPickerVisible = true;
        });

        var provisioner = bridges.Provisioner;
        _provisionerCheck = () => provisioner.CheckSlug;
        provisioner.OfferMode     = OfferModeAsync;
        provisioner.PromptOrgName = ct => _orgName.AskAsync(
            _post, () => OrgNamePromptVisible = true, () => OrgNamePromptVisible = false, ct);
        provisioner.PromptSlug = (suggestion, error, ct) => {
            // The create form asks for the name and the address together: a first ask with an
            // address the form already holds is answered from it, and only a complaint shows it again.
            if (error is null && _orgNameAnswered && !string.IsNullOrWhiteSpace(Slug)) return Task.FromResult<string?>(Slug);

            return _slug.AskAsync(_post, () => {
                if (string.IsNullOrWhiteSpace(Slug)) SetDerivedSlug(suggestion);
                SlugError         = error;
                SlugPromptVisible = true;
            }, () => SlugPromptVisible = false, ct);
        };
        provisioner.ConfirmCreate = (slug, origin, ct) => _confirm.AskAsync(_post, () => {
            ConfirmText    = $"Create {(string.IsNullOrWhiteSpace(OrgName) ? slug : OrgName.Trim())} at {Host(origin)}?";
            ConfirmVisible = true;
        }, () => ConfirmVisible = false, ct);
        provisioner.PollProgress = (attempt, max) =>
            _post(() => ProvisioningProgress = $"Still setting up — checked {attempt} of {max} times.");

        SignInCommand = ReactiveCommand.CreateFromTask(SignInAsync);

        ContinueWithWorkAccountCommand = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Discover, device: false));
        UseCodeCommand                 = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Discover, device: true));
        ToggleUrlPanelCommand          = ReactiveCommand.Create(() => { UrlPanelVisible = !UrlPanelVisible; });
        ContinueWithUrlCommand         = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Paste, device: false));
        CancelCommand = ReactiveCommand.Create(() => _attempt?.Cancel());

        OpenSignInUrlCommand      = ReactiveCommand.Create(() => Open(BrowserUrl));
        OpenVerificationUriCommand = ReactiveCommand.Create(() => Open(VerificationUri));

        ConfirmTenantCommand = ReactiveCommand.Create(() => {
            TenantPickerVisible = false;
            _picker.Select(SelectedTenant);
        });
        CancelTenantCommand = ReactiveCommand.Create(() => {
            TenantPickerVisible = false;
            _picker.Select(null);
        });

        // Unofferable rather than declinable: a blank submit must never end the whole run.
        var hasWorkspace = this.WhenAnyValue(x => x.ExistingWorkspaceInput, input => !string.IsNullOrWhiteSpace(input));
        var hasOrgName   = this.WhenAnyValue(x => x.OrgName, name => !string.IsNullOrWhiteSpace(name));

        CreateWorkspaceCommand      = ReactiveCommand.Create(() => _mode.Answer(new ProvisionMode.Create()));
        UseExistingWorkspaceCommand = ReactiveCommand.Create(
            () => _mode.Answer(new ProvisionMode.Existing(ExistingWorkspaceInput)), hasWorkspace);
        CancelWorkspaceCommand      = ReactiveCommand.Create(() => _mode.Answer(new ProvisionMode.Cancel()));

        SubmitOrgNameCommand = ReactiveCommand.Create(() => _orgName.Answer(OrgName), hasOrgName);
        SubmitCreateFormCommand = ReactiveCommand.Create(SubmitCreateForm,
            this.WhenAnyValue(x => x.OrgName, x => x.Slug, (name, slug) => !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(slug)));
        CancelCreateFormCommand = ReactiveCommand.Create(() => {
            if (OrgNamePromptVisible) _orgName.Answer(null);
            else _slug.Answer(null);
        });
        CancelOrgNameCommand = ReactiveCommand.Create(() => _orgName.Answer(null));
        SubmitSlugCommand    = ReactiveCommand.Create(() => _slug.Answer(Slug));
        CancelSlugCommand    = ReactiveCommand.Create(() => _slug.Answer(null));
        ConfirmCreateCommand = ReactiveCommand.Create(() => {
            // The request and the first status poll are seconds apart; the page says what is
            // happening across them rather than falling back to the sign-in wait.
            ProvisioningProgress = "Requesting your workspace…";
            _confirm.Answer(true);
        });
        DeclineCreateCommand = ReactiveCommand.Create(() => _confirm.Answer(false));

        AcknowledgeQuarantineCommand = ReactiveCommand.CreateFromTask(AcknowledgeQuarantineAsync);
    }

    public WizardStepId Id         => WizardStepId.SignIn;
    public bool         Applicable => true;

    public SignInPhase Phase =>
        Satisfied                                    ? SignInPhase.SignedIn
        : ConfirmVisible                             ? SignInPhase.ConfirmCreate
        : OrgNamePromptVisible || SlugPromptVisible ? SignInPhase.CreateWorkspace
        : ModeChoiceVisible                          ? SignInPhase.NoWorkspace
        : TenantPickerVisible                        ? SignInPhase.PickWorkspace
        : ProvisioningProgress is not null           ? SignInPhase.Provisioning
        : Busy                                       ? SignInPhase.Waiting
        :                                              SignInPhase.Start;

    public string Title => Phase switch {
        SignInPhase.PickWorkspace                                 => "Choose a workspace",
        SignInPhase.NoWorkspace                                   => "No Capacitor workspace yet",
        SignInPhase.CreateWorkspace or SignInPhase.ConfirmCreate => "Create your workspace",
        SignInPhase.Provisioning                                  => "Setting up your workspace",
        SignInPhase.SignedIn                                      => "You're signed in",
        _                                                         => "Sign in to Capacitor",
    };

    public string Eyebrow => Phase is SignInPhase.Start or SignInPhase.Waiting ? "Sign in" : "Your workspace";

    public string? Lede => Phase switch {
        SignInPhase.Start or SignInPhase.Waiting =>
            "Use your work account. It finds the Capacitor workspaces you belong to, or helps you create one.",
        SignInPhase.PickWorkspace =>
            "Your account belongs to more than one. Pick the one this machine records into.",
        SignInPhase.NoWorkspace =>
            "Your account isn't in a Capacitor workspace. Create one now, or point this machine at one you already have.",
        SignInPhase.CreateWorkspace or SignInPhase.ConfirmCreate =>
            "Your team's sessions live here. You can invite people from it once it's ready.",
        SignInPhase.Provisioning => "This usually takes under a minute. Keep this window open.",
        _                        => null,
    };

    /// Sign-in has its own actions on the page. Next stays hidden until it commits, and while a
    /// quarantine notice is still up.
    public bool OwnsPrimaryAction => !Satisfied || QuarantineNotice is not null;

    public bool Skippable => false;

    public ConnectChoiceViewModel Connect => _connect;

    /// Sign-in committed and a quarantine notice, if any, has been acknowledged.
    public event Action? Completed;

    public ObservableCollection<string>           Log     { get; } = [];
    public ObservableCollection<DiscoveredTenant> Tenants { get; } = [];

    public ReactiveCommand<Unit, Unit> SignInCommand                { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand                { get; }
    public ReactiveCommand<Unit, Unit> OpenSignInUrlCommand         { get; }
    public ReactiveCommand<Unit, Unit> OpenVerificationUriCommand   { get; }
    public ReactiveCommand<Unit, Unit> ConfirmTenantCommand         { get; }
    public ReactiveCommand<Unit, Unit> CancelTenantCommand          { get; }
    public ReactiveCommand<Unit, Unit> CreateWorkspaceCommand       { get; }
    public ReactiveCommand<Unit, Unit> UseExistingWorkspaceCommand  { get; }
    public ReactiveCommand<Unit, Unit> CancelWorkspaceCommand       { get; }
    public ReactiveCommand<Unit, Unit> SubmitOrgNameCommand         { get; }
    public ReactiveCommand<Unit, Unit> CancelOrgNameCommand         { get; }
    public ReactiveCommand<Unit, Unit> SubmitSlugCommand            { get; }
    public ReactiveCommand<Unit, Unit> CancelSlugCommand            { get; }
    public ReactiveCommand<Unit, Unit> ConfirmCreateCommand         { get; }
    public ReactiveCommand<Unit, Unit> DeclineCreateCommand         { get; }
    public ReactiveCommand<Unit, Unit> AcknowledgeQuarantineCommand { get; }
    public ReactiveCommand<Unit, Unit> ContinueWithWorkAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> UseCodeCommand               { get; }
    public ReactiveCommand<Unit, Unit> ToggleUrlPanelCommand        { get; }
    public ReactiveCommand<Unit, Unit> ContinueWithUrlCommand       { get; }
    public ReactiveCommand<Unit, Unit> SubmitCreateFormCommand      { get; }
    public ReactiveCommand<Unit, Unit> CancelCreateFormCommand      { get; }

    /// The "I have a workspace URL" field under the work-account action.
    public bool UrlPanelVisible {
        get => _urlPanelVisible;
        set => this.RaiseAndSetIfChanged(ref _urlPanelVisible, value);
    }

    /// The live answer for the address in the create form: "available", "taken", or a reason.
    public string? SlugHint {
        get => _slugHint;
        private set => this.RaiseAndSetIfChanged(ref _slugHint, value);
    }

    public bool SlugAvailable {
        get => _slugAvailable;
        private set => this.RaiseAndSetIfChanged(ref _slugAvailable, value);
    }

    /// The address the form will create, as it would be typed into a browser.
    public string SlugOrigin => string.IsNullOrWhiteSpace(Slug) ? "" : $"{SlugValidator.Canonicalize(Slug)}.kcap.ai";

    public string Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool StatusIsError {
        get => _statusIsError;
        private set {
            this.RaiseAndSetIfChanged(ref _statusIsError, value);
            this.RaisePropertyChanged(nameof(PrimaryActionLabel));
        }
    }

    /// "Try again" after a failure so the primary action is not another "Sign in" next to the title.
    public string PrimaryActionLabel => StatusIsError ? "Try again" : "Sign in";

    /// The last error line the façade rendered. Detail behind the generic failure headline.
    public string? StatusDetail {
        get => _statusDetail;
        private set => this.RaiseAndSetIfChanged(ref _statusDetail, value);
    }

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
            this.RaisePropertyChanged(nameof(ShowPrimaryAction));
            RestatePhase();
        }
    }

    public bool Idle => !Busy;

    public bool Satisfied {
        get => _satisfied;
        private set {
            this.RaiseAndSetIfChanged(ref _satisfied, value);
            this.RaisePropertyChanged(nameof(ShowPrimaryAction));
            this.RaisePropertyChanged(nameof(OwnsPrimaryAction));
            RestatePhase();
        }
    }

    /// Hidden once sign-in committed — the status line is the success state, not another Sign in.
    public bool ShowPrimaryAction => Idle && !Satisfied;

    public string? DeviceCode {
        get => _deviceCode;
        private set => this.RaiseAndSetIfChanged(ref _deviceCode, value);
    }

    public string? VerificationUri {
        get => _verificationUri;
        private set => this.RaiseAndSetIfChanged(ref _verificationUri, value);
    }

    public string? BrowserUrl {
        get => _browserUrl;
        private set => this.RaiseAndSetIfChanged(ref _browserUrl, value);
    }

    public string? WaitingText {
        get => _waitingText;
        private set => this.RaiseAndSetIfChanged(ref _waitingText, value);
    }

    public string? QuarantineNotice {
        get => _quarantineNotice;
        private set {
            this.RaiseAndSetIfChanged(ref _quarantineNotice, value);
            this.RaisePropertyChanged(nameof(OwnsPrimaryAction));
        }
    }

    public bool TenantPickerVisible {
        get => _tenantPickerVisible;
        private set {
            this.RaiseAndSetIfChanged(ref _tenantPickerVisible, value);
            RestatePhase();
        }
    }

    public DiscoveredTenant? SelectedTenant {
        get => _selectedTenant;
        set => this.RaiseAndSetIfChanged(ref _selectedTenant, value);
    }

    public bool ModeChoiceVisible {
        get => _modeChoiceVisible;
        private set {
            this.RaiseAndSetIfChanged(ref _modeChoiceVisible, value);
            RestatePhase();
        }
    }

    public bool OrgNamePromptVisible {
        get => _orgNamePromptVisible;
        private set {
            this.RaiseAndSetIfChanged(ref _orgNamePromptVisible, value);
            RestatePhase();
        }
    }

    public bool SlugPromptVisible {
        get => _slugPromptVisible;
        private set {
            this.RaiseAndSetIfChanged(ref _slugPromptVisible, value);
            RestatePhase();
        }
    }

    public bool ConfirmVisible {
        get => _confirmVisible;
        private set {
            this.RaiseAndSetIfChanged(ref _confirmVisible, value);
            RestatePhase();
        }
    }

    public string OrgName {
        get => _orgNameText;
        set {
            this.RaiseAndSetIfChanged(ref _orgNameText, value);
            // The address follows the name until the user types one of their own.
            if (!_slugEdited) SetDerivedSlug(SlugValidator.Derive(value ?? ""));
        }
    }

    public string Slug {
        get => _slugText;
        set {
            this.RaiseAndSetIfChanged(ref _slugText, value);
            if (!_settingDerivedSlug) _slugEdited = !string.IsNullOrWhiteSpace(value);
            this.RaisePropertyChanged(nameof(SlugOrigin));
            SlugError = null;
            ScheduleSlugCheck(value);
        }
    }

    public string? SlugError {
        get => _slugError;
        private set => this.RaiseAndSetIfChanged(ref _slugError, value);
    }

    public string ExistingWorkspaceInput {
        get => _existingWorkspaceInput;
        set => this.RaiseAndSetIfChanged(ref _existingWorkspaceInput, value);
    }

    public string ConfirmText {
        get => _confirmText;
        private set => this.RaiseAndSetIfChanged(ref _confirmText, value);
    }

    public string? ProvisioningProgress {
        get => _provisioningProgress;
        private set {
            this.RaiseAndSetIfChanged(ref _provisioningProgress, value);
            RestatePhase();
        }
    }

    public Task OnEnterAsync(CancellationToken ct) {
        if (!Satisfied && !Busy) {
            SetStatus(ReadyStatus(), isError: false, ReadyDetail());
            _statusIsReady = true;
            this.RaisePropertyChanged(nameof(StatusLineVisible));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A live attempt is cancelled, and the RUN — not just the attempt — is awaited:
    /// pre-boundary that ends it with nothing durable, past the boundary the operation still
    /// answers Committed, and either way the step's rendered state is final before the wizard
    /// moves. Forward is refused while a quarantine notice is up.
    /// </summary>
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        _attempt?.Cancel();

        if (_run is { } run) {
            try {
                await run.ConfigureAwait(true);
            } catch (Exception ex) {
                // A run that failed unexpectedly must not veto the navigation.
                Console.Error.WriteLine($"kcap: wizard sign-in run failed unexpectedly: {ex.Message}");
            }
        }

        return direction == WizardNavigation.Back || QuarantineNotice is null;
    }

    /// The command's body, reachable directly so a re-entrant call can be asserted as a no-op.
    internal Task SignInAsync() {
        // Busy is set before RunAsync's first await, so a re-entrant call never displaces _run.
        if (Busy) return Task.CompletedTask;

        return _run = RunAsync();
    }

    async Task RunAsync() {
        if (_connect.Intent is not { } intent) {
            _connect.Validate();
            UrlPanelVisible = true;

            return;
        }

        ResetForRun(intent);
        Busy = true;
        // Before Begin: a synchronously-rendered error must not have its detail wiped by this.
        SetStatus(
            "Waiting for your browser…",
            isError: false,
            "Complete authorization there, then return here. This window updates on its own.");

        AuthAttempt attempt;

        try {
            attempt = _service.Begin(intent);
        } catch (InvalidOperationException) {
            Busy = false;
            SetStatus("Finishing the previous attempt. Try again in a moment.", isError: false);

            return;
        }

        _attempt = attempt;

        var result = await attempt.Result.ConfigureAwait(true);

        _attempt = null;
        Busy     = false;
        HidePrompts();
        // Nothing polls a device code or a browser wait once the attempt has settled.
        ClearTransient();
        Apply(result);
        await SurfaceQuarantineAsync().ConfigureAwait(true);

        if (Satisfied && QuarantineNotice is null) Completed?.Invoke();
    }

    Task<ProvisionMode> OfferModeAsync(CancellationToken ct) =>
        _mode.AskAsync(_post, () => ModeChoiceVisible = true, () => ModeChoiceVisible = false, ct);

    void Apply(AuthResult result) {
        switch (result) {
            case AuthResult.Committed { CredentialSaved: false }:
                SetStatus("Signed in, but the credential could not be saved.", isError: true,
                    _lastReport ?? "Sign in again from the profile's row in Settings.");

                break;
            case AuthResult.Committed committed:
                Satisfied = true;
                SetStatus(CommittedStatus(committed), isError: false, _committedDetail);

                break;
            case AuthResult.Cancelled:
                SetStatus("Sign-in cancelled.", isError: false);

                break;
            case AuthResult.Retarget retarget:
                _connect.Prefill(retarget.ServerInput);
                UrlPanelVisible = true;
                SetStatus($"Sign in to {retarget.ServerInput}", isError: false,
                    "Continue below to sign in to that workspace instead.");

                break;
            // Provisioning outran its poll window. Sign-in itself succeeded and the workspace is on its
            // way, so headlining a failure here would tell the user something untrue.
            case AuthResult.Failed { Reason: AuthFailureReason.ProvisioningInProgress } pending:
                SetStatus(pending.Message, isError: false, _lastReport);

                break;
            // Already rendered through the sink; prefer the last reported line over in-flight
            // guidance (StatusDetail holds browser-wait copy until an ErrorReceived overwrites it).
            default:
                SetStatus("Sign-in failed.", isError: true, _lastReport ?? StatusDetail);

                break;
        }
    }

    static string CommittedStatus(AuthResult.Committed committed) =>
        committed.Provider == AuthProvider.None
            ? "No sign-in required for this server."
            : committed.Username is { Length: > 0 } username ? $"Signed in as {username}" : "Signed in.";

    string ReadyStatus() => _connect.Intent switch {
        // Destination only — the surrounding chrome already says "Sign in"; detail explains what happens.
        ConnectIntent.Paste paste => paste.ServerInput,
        _                         => "Find your workspaces with single sign-on",
    };

    string ReadyDetail() => _connect.Intent switch {
        ConnectIntent.Paste =>
            "Opens your browser to authorize this machine and stores a token for launching hosted agents.",
        _ => "Opens your browser, then lists the workspaces your account can access.",
    };

    async Task SurfaceQuarantineAsync() {
        try {
            // A read is what discovers corruption, and a cancelled attempt never armed anything.
            await Task.Run(_claims.Pending).ConfigureAwait(true);

            if (_claims.Quarantine() is not { } quarantine) return;
            if ((await _appState.LoadAsync().ConfigureAwait(true)).ConsentQuarantineAcked) return;

            QuarantineNotice = ConsentFlipCoordinator.QuarantineDisclosure(quarantine.PreservedPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent quarantine surfacing failed unexpectedly: {ex.Message}");
        }
    }

    async Task AcknowledgeQuarantineAsync() {
        QuarantineNotice = null;

        try {
            await ConsentFlipCoordinator.AckQuarantineAsync(_appState).ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent quarantine ack failed unexpectedly: {ex.Message}");
        }

        if (Satisfied) Completed?.Invoke();
    }

    Task StartAsync(ConnectChoice choice, bool device) {
        if (Busy) return Task.CompletedTask;

        _connect.Choice        = choice;
        _connect.UseDeviceCode = device;
        if (choice == ConnectChoice.Paste && !_connect.Validate()) return Task.CompletedTask;

        return SignInAsync();
    }

    void SubmitCreateForm() {
        if (OrgNamePromptVisible) {
            _orgNameAnswered = true;
            _orgName.Answer(OrgName);
        } else {
            _slug.Answer(Slug);
        }
    }

    internal static readonly TimeSpan SlugCheckDelay = TimeSpan.FromMilliseconds(400);

    CancellationTokenSource? _slugCheck;

    // A check per pause in typing, never per keystroke; an answer for an address the user has
    // since changed is dropped.
    void ScheduleSlugCheck(string? slug) {
        _slugCheck?.Cancel();
        SlugHint      = null;
        SlugAvailable = false;
        if (_provisionerCheck() is null || string.IsNullOrWhiteSpace(slug)) return;

        var cts = _slugCheck = new CancellationTokenSource();
        _ = CheckSlugAsync(SlugValidator.Canonicalize(slug), cts.Token);
    }

    async Task CheckSlugAsync(string slug, CancellationToken ct) {
        try {
            await Task.Delay(SlugCheckDelay, _time, ct).ConfigureAwait(false);
            var shape = SlugValidator.Validate(slug);
            if (!shape.Ok) {
                Report(false, shape.Reason == "blocked" ? "reserved" : "lowercase letters, digits and hyphens");
                return;
            }

            if (_provisionerCheck() is not { } check) return;
            var answer = await check(slug, ct).ConfigureAwait(false);
            if (answer is null) return;

            Report(answer.Available || answer.Reason == "yours", answer.Available || answer.Reason == "yours"
                ? "available"
                : answer.Reason switch {
                    "reserved" => "being set up by someone else",
                    "blocked"  => "reserved",
                    _          => "taken",
                });
        } catch (OperationCanceledException) {
            // superseded by a newer address
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: slug availability check failed: {ex.Message}");
        }

        void Report(bool available, string hint) => _post(() => {
            if (ct.IsCancellationRequested) return;
            SlugAvailable = available;
            SlugHint      = hint;
        });
    }

    void SetDerivedSlug(string slug) {
        _settingDerivedSlug = true;
        try {
            Slug = slug;
        } finally {
            _settingDerivedSlug = false;
        }
    }

    static string Host(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;

    public bool StartPanelVisible   => Phase is SignInPhase.Start or SignInPhase.Waiting;
    public bool CreateFormVisible   => Phase is SignInPhase.CreateWorkspace;
    public bool ProvisioningVisible => Phase is SignInPhase.Provisioning;
    public bool SignedInVisible     => Phase is SignInPhase.SignedIn;

    /// The status line under the work-account action. The ready line only restates the button,
    /// so it stays hidden until something has happened.
    public bool StatusLineVisible => !_statusIsReady && !string.IsNullOrEmpty(Status);

    void RestatePhase() {
        this.RaisePropertyChanged(nameof(StartPanelVisible));
        this.RaisePropertyChanged(nameof(CreateFormVisible));
        this.RaisePropertyChanged(nameof(ProvisioningVisible));
        this.RaisePropertyChanged(nameof(SignedInVisible));
        this.RaisePropertyChanged(nameof(Phase));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Eyebrow));
        this.RaisePropertyChanged(nameof(Lede));
    }

    void ResetForRun(ConnectIntent intent) {
        _running         = intent;
        _orgNameAnswered = false;
        _lastReport = null;
        Log.Clear();
        HidePrompts();
        ClearTransient();
        SlugError    = null;
        StatusDetail = null;
    }

    void ClearTransient() {
        DeviceCode           = null;
        VerificationUri      = null;
        BrowserUrl           = null;
        WaitingText          = null;
        ProvisioningProgress = null;
    }

    void HidePrompts() {
        TenantPickerVisible  = false;
        ModeChoiceVisible    = false;
        OrgNamePromptVisible = false;
        SlugPromptVisible    = false;
        ConfirmVisible       = false;
    }

    void SetStatus(string text, bool isError, string? detail = null) {
        _statusIsReady = false;
        Status        = text;
        this.RaisePropertyChanged(nameof(StatusLineVisible));
        StatusIsError = isError;
        StatusDetail  = detail is null ? null : FormatErrorDetail(detail);
    }

    void Append(string line) {
        Log.Add(line);

        while (Log.Count > LogLimit) Log.RemoveAt(0);
    }

    void Open(string? url) {
        if (string.IsNullOrEmpty(url)) return;

        try {
            _urlOpener.Open(url);
        } catch (Exception ex) {
            Append($"Couldn't open the browser: {ex.Message}");
        }
    }

    /// The prominent display shows the code alone; the log keeps the line the flow emitted.
    internal static string StripClipboardNote(string code) {
        var note = code.IndexOf("  (copied", StringComparison.Ordinal);

        return note < 0 ? code.Trim() : code[..note].Trim();
    }

    /// Progress sink lines often start with "Error: "; the failure headline already says that.
    internal static string FormatErrorDetail(string line) {
        const string prefix = "Error: ";
        var text = line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].TrimStart()
            : line.Trim();
        if (text.Length == 0) return text;

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
