using System.Collections.ObjectModel;
using System.Reactive;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One cancellable auth attempt: discovery or a pasted server.
public sealed class SignInStepViewModel : ReactiveObject, IWizardStep {
    internal const int LogLimit = 200;

    /// How long the re-auth dialog keeps the success line up before it closes.
    internal static readonly TimeSpan SuccessHold = TimeSpan.FromMilliseconds(1600);

    /// How long the page waits on a browser sign-up: twice kcap-web's 15-minute provisioning budget,
    /// so a slow provision still lands inside it.
    internal static readonly TimeSpan SignUpWait = TimeSpan.FromMinutes(30);

    public const string SignUpExpiredNotice =
        "Sign-up in your browser didn't finish in time. Choose an edition to start again.";

    public const string WorkspaceNotReady = "Your workspace isn't ready yet.";

    public const string WorkspaceSettingUp = "Your workspace is still being set up";

    readonly WizardAuthService       _service;
    readonly ConnectChoiceViewModel  _connect;
    readonly WizardTenantPicker      _picker;
    readonly UiAuthProgress          _progress;
    readonly ConsentFlipClaims       _claims;
    readonly IAppStateStore          _appState;
    readonly IUrlOpener              _urlOpener;
    readonly Action<Action>          _post;
    readonly string?                 _committedDetail;
    readonly TimeProvider            _time;
    readonly Func<bool>?             _passThrough;
    readonly Func<bool>              _signingUp;
    readonly string                  _signUpUrl;
    string? _workspace;
    public string ConnectionStatus => Uri.TryCreate(_workspace, UriKind.Absolute, out var uri)
        ? $"Connected to {uri.Host}" : "Connected to your workspace";

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
    bool    _tenantChoiceMade;
    bool    _urlPanelVisible;
    bool    _statusIsReady;
    bool    _expireAfterRun;

    CancellationTokenSource? _signUpClock;

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
            TimeProvider?        time = null,
            // True while this run is on the local path, where there is nothing to sign in to.
            Func<bool>?          passThrough = null,
            // True while this run is on the cloud path, where the workspace is created in the browser.
            Func<bool>?          signingUp = null) {
        _time            = time ?? TimeProvider.System;
        _passThrough     = passThrough;
        _signingUp       = signingUp ?? (() => false);
        _signUpUrl       = SignUpUrl(bridges.Endpoints.SignupUrl);
        _service         = service;
        _connect         = connect;
        _picker          = bridges.Picker;
        _progress        = bridges.Progress;
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
            Append(prefilled
                ? $"Check the code shown is {code} at {verificationUri}"
                : $"Enter the code {code} at {verificationUri}");
        };
        bridges.Progress.PollTicked += () => WaitingText = "Waiting for you to authorize…";
        bridges.Progress.WorkspaceProvisioning += workspace => {
            if (!Busy) return;

            ClearTransient();
            SetStatus(WorkspaceSettingUp, isError: false, WorkspaceSettingUpDetail(workspace));
        };

        _picker.SelectionRequested += tenants => _post(() => {
            Tenants.Clear();
            foreach (var tenant in tenants) Tenants.Add(tenant);
            SelectedTenant      = Tenants.FirstOrDefault();
            _tenantChoiceMade   = false;
            TenantPickerVisible = true;
        });

        SignInCommand = ReactiveCommand.CreateFromTask(SignInAsync);

        ContinueWithWorkAccountCommand = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Discover, device: false));
        UseCodeCommand                 = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Discover, device: true));
        ToggleUrlPanelCommand          = ReactiveCommand.Create(() => { UrlPanelVisible = !UrlPanelVisible; });
        ContinueWithUrlCommand         = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Paste, device: false));
        ContinueAfterSignUpCommand     = ReactiveCommand.CreateFromTask(() => StartAsync(ConnectChoice.Discover, device: false));
        ReopenSignUpCommand            = ReactiveCommand.Create(OpenSignUp);
        CancelCommand = ReactiveCommand.Create(() => _attempt?.Cancel());

        OpenSignInUrlCommand      = ReactiveCommand.Create(() => Open(BrowserUrl));
        OpenVerificationUriCommand = ReactiveCommand.Create(() => Open(VerificationUri));

        ConfirmTenantCommand = ReactiveCommand.Create(() => CommitTenant(SelectedTenant));
        CancelTenantCommand = ReactiveCommand.Create(() => {
            _tenantChoiceMade = true;
            this.RaisePropertyChanged(nameof(TenantChoicePending));
            _attempt?.Cancel();
        });

        AcknowledgeQuarantineCommand = ReactiveCommand.CreateFromTask(AcknowledgeQuarantineAsync);
    }

    public WizardStepId Id         => WizardStepId.SignIn;
    public bool         Applicable => true;

    public SignInPhase Phase =>
        TenantPickerVisible          ? SignInPhase.PickWorkspace
        : Busy                       ? SignInPhase.Waiting
        : _signingUp() && !Satisfied ? SignInPhase.SignUp
        :                              SignInPhase.Start;

    public string Title => Phase switch {
        SignInPhase.PickWorkspace => "Choose a workspace",
        SignInPhase.SignUp        => "Finish in your browser, then continue here",
        _                         => "Sign in to kcap",
    };

    public string Eyebrow => Phase switch {
        SignInPhase.PickWorkspace => "Your workspace",
        SignInPhase.SignUp        => "Sign up",
        _                         => "Sign in",
    };

    public string? Lede => Phase switch {
        SignInPhase.SignUp =>
            "Create your workspace on the kcap.ai page that just opened. When it says your workspace is ready, " +
            "continue here and setup carries on.",
        SignInPhase.Start or SignInPhase.Waiting =>
            "Use your work account. It finds the kcap workspaces you belong to.",
        SignInPhase.PickWorkspace when Tenants.Count == 1 =>
            "Check this is the workspace you just created, then use it on this machine.",
        SignInPhase.PickWorkspace =>
            "Your account belongs to more than one. Pick the one this machine records into.",
        _ => null,
    };

    /// Sign-in has its own actions on the page. Next stays hidden until it commits, and while a
    /// quarantine notice is still up.
    public bool OwnsPrimaryAction => !Satisfied || QuarantineNotice is not null;
    public bool Skippable => false;
    public bool PassThrough => _passThrough?.Invoke() ?? false;

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
    public ReactiveCommand<Unit, Unit> AcknowledgeQuarantineCommand { get; }
    public ReactiveCommand<Unit, Unit> ContinueWithWorkAccountCommand { get; }
    public ReactiveCommand<Unit, Unit> UseCodeCommand               { get; }
    public ReactiveCommand<Unit, Unit> ToggleUrlPanelCommand        { get; }
    public ReactiveCommand<Unit, Unit> ContinueWithUrlCommand       { get; }
    public ReactiveCommand<Unit, Unit> ContinueAfterSignUpCommand   { get; }
    public ReactiveCommand<Unit, Unit> ReopenSignUpCommand          { get; }

    /// The browser sign-up outlived <see cref="SignUpWait"/> without a workspace signed in to.
    public event Action? SignUpExpired;

    /// The "I have a workspace URL" field under the work-account action.
    public bool UrlPanelVisible {
        get => _urlPanelVisible;
        set => this.RaiseAndSetIfChanged(ref _urlPanelVisible, value);
    }

    public string Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool StatusIsError {
        get => _statusIsError;
        private set {
            this.RaiseAndSetIfChanged(ref _statusIsError, value);
            this.RaisePropertyChanged(nameof(PrimaryActionLabel));
            this.RaisePropertyChanged(nameof(WorkAccountLabel));
        }
    }

    /// "Try again" after a failure so the primary action is not another "Sign in" next to the title.
    public string PrimaryActionLabel => StatusIsError ? "Try again" : "Sign in";
    public string WorkAccountLabel => StatusIsError ? "Try again" : "Continue with work account";

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
            this.RaisePropertyChanged(nameof(StatusLineVisible));
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
            this.RaisePropertyChanged(nameof(TenantChoicePending));
            RestatePhase();
        }
    }

    /// Footer actions for the list. False once a choice is in flight, while the list itself stays.
    public bool TenantChoicePending => _tenantPickerVisible && !_tenantChoiceMade;

    public DiscoveredTenant? SelectedTenant {
        get => _selectedTenant;
        set => this.RaiseAndSetIfChanged(ref _selectedTenant, value);
    }

    public Task OnEnterAsync(CancellationToken ct) {
        if (!Satisfied && !Busy) {
            SetStatus(ReadyStatus(), isError: false, ReadyDetail());
            _statusIsReady = true;
            this.RaisePropertyChanged(nameof(StatusLineVisible));

            if (_signingUp()) {
                OpenSignUp();
                StartSignUpClock();
            }
        }

        RestatePhase();

        return Task.CompletedTask;
    }

    /// kcap-web's Team sign-up; <c>via=desktop</c> is what it records as the desktop setup path.
    internal static string SignUpUrl(string signupBase) => $"{signupBase.TrimEnd('/')}/get-started?tier=team&via=desktop";

    void OpenSignUp() {
        try {
            _urlOpener.Open(_signUpUrl);
            RememberWebSignUp(_time.GetUtcNow());
        } catch (Exception ex) {
            Append($"Couldn't open the browser: {ex.Message}");
            SetStatus("Couldn't open your browser.", isError: true,
                $"Open {_signUpUrl} to create your workspace, then continue here.");
        }
    }

    // A relaunch while the workspace is being created on kcap.ai opens on sign-in rather than on the edition choice.
    void RememberWebSignUp(DateTimeOffset? openedAt) {
        _ = PersistAsync();

        async Task PersistAsync() {
            try {
                if (openedAt is null && (await _appState.LoadAsync().ConfigureAwait(false)).WebSignUpOpenedAt is null) return;

                await _appState.UpdateAsync(state => state with { WebSignUpOpenedAt = openedAt }).ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: could not record the browser sign-up: {ex.Message}");
            }
        }
    }

    /// The wizard closed: nothing is left to return to the edition screen.
    internal void EndSignUpWait() => StopSignUpClock();

    void StartSignUpClock() {
        StopSignUpClock();
        var clock = _signUpClock = new CancellationTokenSource();
        _ = ExpireAsync(clock.Token);
    }

    void StopSignUpClock() {
        _signUpClock?.Cancel();
        _signUpClock    = null;
        _expireAfterRun = false;
    }

    async Task ExpireAsync(CancellationToken ct) {
        try {
            await Task.Delay(SignUpWait, _time, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        _post(() => {
            if (ct.IsCancellationRequested || Satisfied) return;
            // A Continue in flight is cancelled and decides first: a commit already past its boundary
            // still ends the wait, anything else expires it.
            if (Busy) {
                _expireAfterRun = true;
                _attempt?.Cancel();
            } else {
                SignUpExpired?.Invoke();
            }
        });
    }

    internal void RestoreCommitted() {
        Satisfied = true;
        SetStatus("Connected to your workspace", isError: false);
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

        var leaving = direction == WizardNavigation.Back || Satisfied && QuarantineNotice is null;
        // A refused move leaves the user on the wait, so its clock keeps running.
        if (leaving) StopSignUpClock();

        return leaving;
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

        // A browser sign-up's new workspace is left out until it is live, so a lone workspace found
        // now may be another the account belongs to.
        if (_signingUp() && intent is ConnectIntent.Discover discover) intent = discover with { ConfirmPick = true };

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
        Apply(result);
        if (!Satisfied || !TenantPickerVisible) HidePrompts();
        ClearTransient();
        Busy = false;
        await SurfaceQuarantineAsync().ConfigureAwait(true);

        if (Satisfied) StopSignUpClock();
        if (_expireAfterRun) {
            _expireAfterRun = false;
            SignUpExpired?.Invoke();
        }

        if (Satisfied && QuarantineNotice is null) Completed?.Invoke();
    }

    void Apply(AuthResult result) {
        var failedWorkspace = _progress.TakeProvisioningFailure();
        switch (result) {
            case AuthResult.Committed { CredentialSaved: false }:
                SetStatus("Signed in, but the credential could not be saved.", isError: true,
                    _lastReport ?? "Sign in again from the profile's row in Settings.");

                break;
            case AuthResult.Committed committed:
                _workspace = committed.CanonicalServer;
                this.RaisePropertyChanged(nameof(ConnectionStatus));
                Satisfied = true;
                RememberWebSignUp(null);
                SetStatus(CommittedStatus(committed), isError: false, _committedDetail);

                break;
            case AuthResult.Cancelled:
                SetStatus("Sign-in cancelled.", isError: false);

                break;
            // The wait on a workspace still being created ran out before it went live.
            case AuthResult.Failed { Reason: AuthFailureReason.ProvisioningInProgress }:
                SetStatus(WorkspaceSettingUp, isError: false, WorkspaceStillSettingUpDetail);

                break;
            case AuthResult.Failed { Reason: AuthFailureReason.NoTenantsFound } when failedWorkspace is not null:
                SetStatus(WorkspaceNotCreated, isError: true, WorkspaceNotCreatedDetail(failedWorkspace));

                break;
            // A browser sign-up still provisioning: the wait goes on, so this is not a failed sign-in.
            case AuthResult.Failed { Reason: AuthFailureReason.NoTenantsFound } when _signingUp():
                SetStatus(WorkspaceNotReady, isError: false,
                    "Finish creating it in your browser, then continue here. Setting it up can take a few minutes.");

                break;
            // Already rendered through the sink; prefer the last reported line over in-flight
            // guidance (StatusDetail holds browser-wait copy until an ErrorReceived overwrites it).
            default:
                SetStatus("Sign-in failed.", isError: true, _lastReport ?? StatusDetail);

                break;
        }
    }

    internal const string WorkspaceNotCreated = "kcap.ai couldn't finish creating your workspace";

    internal static string WorkspaceNotCreatedDetail(string workspace) =>
        $"{workspace} didn't finish setting up. Its page on kcap.ai says what happened";

    internal const string WorkspaceStillSettingUpDetail =
        "It's taking longer than usual. Sign in again in a few minutes, once kcap.ai says it's ready";

    internal static string WorkspaceSettingUpDetail(string workspace) =>
        $"{workspace} is still being created on kcap.ai. It usually takes a few minutes, and sign-in carries on by itself once it's ready";

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

    public bool StartPanelVisible  => Phase is SignInPhase.Start or SignInPhase.Waiting;
    public bool SignUpPanelVisible => Phase is SignInPhase.SignUp;

    /// The status line under the work-account action. The ready line only restates the button,
    /// so it stays hidden until something has happened. A committed result uses the success row.
    public bool StatusLineVisible => !_statusIsReady && !Satisfied && !string.IsNullOrEmpty(Status);

    void RestatePhase() {
        this.RaisePropertyChanged(nameof(StartPanelVisible));
        this.RaisePropertyChanged(nameof(SignUpPanelVisible));
        this.RaisePropertyChanged(nameof(Phase));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Eyebrow));
        this.RaisePropertyChanged(nameof(Lede));
    }

    void ResetForRun(ConnectIntent intent) {
        _running    = intent;
        _lastReport = null;
        _progress.TakeProvisioningFailure();
        Log.Clear();
        HidePrompts();
        ClearTransient();
        StatusDetail = null;
    }

    void ClearTransient() {
        DeviceCode      = null;
        VerificationUri = null;
        BrowserUrl      = null;
        WaitingText     = null;
    }

    void CommitTenant(DiscoveredTenant? tenant) {
        if (_tenantChoiceMade) return;

        _tenantChoiceMade = true;
        this.RaisePropertyChanged(nameof(TenantChoicePending));
        _picker.Select(tenant);
    }

    void HidePrompts() {
        _tenantChoiceMade   = false;
        TenantPickerVisible = false;
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
        if (text.Length == 0 || text.StartsWith("kcap ", StringComparison.Ordinal)) return text;

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
