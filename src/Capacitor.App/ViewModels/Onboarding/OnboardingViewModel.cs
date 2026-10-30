using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services.Onboarding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// Back/Next/Skip all funnel through Current's CanLeaveAsync veto; Next on the last step finishes.
public sealed class OnboardingViewModel : ReactiveObject {
    readonly CancellationToken _shutdownToken;
    readonly Subject<Unit>     _restated = new();
    bool _closed;
    bool _navigating;
    bool _advancePending;
    bool _finishLaterAllowed = true;

    internal bool Closed => _closed;

    /// The user chose to leave setup and the app with it, rather than defer setup.
    internal bool QuitRequested { get; private set; }

    /// Null where nothing gates the first step.
    public HarnessPreflight? Preflight { get; }

    /// The pre-flight's own card stands in for the step, its header and its footer.
    public bool PreflightChecking => Preflight is { State: HarnessPreflightState.Checking };
    public bool PreflightBlocked  => Preflight is { State: HarnessPreflightState.NoneFound };
    public bool StepVisible       => Preflight is null or { State: HarnessPreflightState.Passed };

    /// Whether setup may be left half-done, the gate Finish later shows behind.
    internal bool Deferrable => _finishLaterAllowed;

    public IReadOnlyList<IWizardStep> Steps { get; }

    /// Wizard-first mode builds no tray and no main window, so the outcome consumer's Status/
    /// Attention lines are rendered by the Done page's service card. Null in tests that don't need them.
    public WizardLifecycleSurface? Surface { get; }

    int _index;
    readonly bool[] _visited;

    IWizardStep? _current;
    public IWizardStep Current {
        get => _current!;
        private set {
            if (_current is not null) _current.AdvanceRequested -= OnAdvanceRequested;
            if (_current is INotifyPropertyChanged leaving) leaving.PropertyChanged -= OnCurrentChanged;
            _advancePending = false;
            this.RaiseAndSetIfChanged(ref _current, value);
            if (value is INotifyPropertyChanged entering) entering.PropertyChanged += OnCurrentChanged;
            value.AdvanceRequested += OnAdvanceRequested;
            this.RaisePropertyChanged(nameof(Eyebrow));
            this.RaisePropertyChanged(nameof(NextLabel));
            this.RaisePropertyChanged(nameof(SkipLabel));
            RestateActions();
        }
    }

    /// Counts the pages this run shows: a page it passes through is not a step, unless it was already shown
    /// before it began passing through.
    public string Eyebrow {
        get {
            var (position, total) = (0, 0);
            for (var i = 0; i < Steps.Count; i++) {
                if (!_visited[i] && Steps[i].PassThrough) continue;
                total++;
                if (i <= _index) position++;
            }
            return $"STEP {position} OF {total} · {Current.Eyebrow.ToUpper(CultureInfo.CurrentCulture)}";
        }
    }

    public string NextLabel => Current.NextLabel ?? (_index == Steps.Count - 1 ? "Get started" : "Next");
    public string SkipLabel => Current.SkipLabel;
    public bool NextVisible => !Current.OwnsPrimaryAction;
    public bool SkipVisible => _index < Steps.Count - 1 && Current.Skippable && !Current.OwnsPrimaryAction;
    public bool BackVisible => Current.BackVisible && PreviousShownIndex() >= 0;
    public bool FooterVisible => StepVisible && (BackVisible || NextVisible || SkipVisible || TenantActionsVisible);
    /// Hidden, not disabled, where setup cannot be left half-done, and where the step's own primary
    /// action already closes setup.
    public bool FinishLaterVisible => StepVisible && _finishLaterAllowed && Current.OffersFinishLater;
    public bool CanGoNext => StepVisible && !Navigating && !_closed && Current.CanContinue;
    public bool CanGoSkip => StepVisible && !Navigating && !_closed && Current.CanSkip && _index < Steps.Count - 1;

    /// The workspace list's own actions share the footer line with Back.
    public bool TenantActionsVisible => Current is SignInStepViewModel { TenantChoicePending: true };

    public System.Windows.Input.ICommand? ConfirmTenantCommand =>
        Current is SignInStepViewModel signIn ? signIn.ConfirmTenantCommand : null;

    public System.Windows.Input.ICommand? CancelTenantCommand =>
        Current is SignInStepViewModel signIn ? signIn.CancelTenantCommand : null;

    /// The Done page's web workspace or local dashboard, as a link beside its primary action.
    public bool OpenInBrowserVisible => Current is DoneStepViewModel { OpenInBrowserVisible: true };

    public System.Windows.Input.ICommand? OpenInBrowserCommand =>
        Current is DoneStepViewModel done ? done.OpenInBrowserCommand : null;

    /// One filled button per screen. Next is that button on a step with no action of its own, and
    /// once the step's own action has finished. While the action is on screen, Next is secondary.
    public bool NextIsPrimary => !Current.ShowsOwnPrimary;

    public bool NextIsSecondary => Current.ShowsOwnPrimary;

    // Shared across Back/Next/Skip: only one of the three may be mid-transition at a time.
    internal bool Navigating {
        get => _navigating;
        set {
            this.RaiseAndSetIfChanged(ref _navigating, value);
            this.RaisePropertyChanged(nameof(CanGoNext));
            this.RaisePropertyChanged(nameof(CanGoSkip));
        }
    }

    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipCommand { get; }
    public ReactiveCommand<Unit, Unit> FinishLaterCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }

    /// Fires once per logical close: the Done step's finish, or the window closing.
    public event Action? CloseRequested;

    /// The constructor's own initial-entry call, exposed so a test can await it deterministically.
    internal Task PendingEnterForTesting { get; }

    internal Task PendingPreflightForTesting { get; } = Task.CompletedTask;

    /// The navigation the current step's own action last started, so a test can await it.
    internal Task PendingAdvanceForTesting { get; private set; } = Task.CompletedTask;

    public OnboardingViewModel(
            IEnumerable<IWizardStep> steps, CancellationToken shutdownToken = default,
            WizardLifecycleSurface? surface = null, WizardStepId? startAt = null,
            IObservable<bool>? canFinishLater = null, HarnessPreflight? preflight = null, IObservable<Unit>? restate = null) {
        _shutdownToken = shutdownToken;
        Surface = surface;
        Preflight = preflight;
        var applicable = steps.Where(s => s.Applicable).ToList();
        Steps = applicable;
        if (Steps.Count == 0) throw new ArgumentException("at least one applicable step is required", nameof(steps));

        _visited = new bool[Steps.Count];
        // A run starts on the first page that offers a choice.
        _index = startAt is { } id ? Math.Max(0, applicable.FindIndex(s => s.Id == id)) : FirstShownFrom(0);
        _visited[_index] = true;
        Current = Steps[_index];

        var currentChanged = this.WhenAnyValue(x => x.Current);
        var idle = this.WhenAnyValue(x => x.Navigating).Select(busy => !busy);
        var canBack = currentChanged.CombineLatest(idle, _restated.StartWith(Unit.Default),
            (_, notBusy, _) => notBusy && PreviousShownIndex() >= 0);

        BackCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Back), canBack);
        SkipCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Skip),
            this.WhenAnyValue(x => x.CanGoSkip));
        NextCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Next),
            this.WhenAnyValue(x => x.CanGoNext));
        var finishLaterAllowed = (canFinishLater ?? Observable.Return(true))
            .Do(allowed => {
                _finishLaterAllowed = allowed;
                this.RaisePropertyChanged(nameof(FinishLaterVisible));
            });
        FinishLaterCommand = ReactiveCommand.Create(RequestClose,
            idle.CombineLatest(finishLaterAllowed, (notBusy, allowed) => notBusy && allowed));

        QuitCommand = ReactiveCommand.Create(() => {
            QuitRequested = true;
            RequestClose();
        });

        if (preflight is not null) {
            preflight.WhenAnyValue(x => x.State).Subscribe(_ => {
                this.RaisePropertyChanged(nameof(PreflightChecking));
                this.RaisePropertyChanged(nameof(PreflightBlocked));
                this.RaisePropertyChanged(nameof(StepVisible));
                RestateActions();
            });
            PendingPreflightForTesting = preflight.RunAsync(shutdownToken);
        }

        // State other steps' PassThrough reads, changed outside the current step.
        restate?.Subscribe(_ => RestateActions());

        PendingEnterForTesting = SafeEnterAsync(Current);
    }

    void OnAdvanceRequested() {
        if (_navigating) _advancePending = true;
        else PendingAdvanceForTesting = NavigateAsync(WizardNavigation.Next);
    }

    // The advance a step raised mid-navigation runs once, after the gate it was refused at opens.
    void ReleaseGate() {
        Navigating = false;
        if (!_advancePending) return;

        _advancePending = false;
        if (!_closed) PendingAdvanceForTesting = NavigateAsync(WizardNavigation.Next);
    }

    int PreviousShownIndex() {
        for (var i = _index - 1; i >= 0; i--) {
            if (!Steps[i].PassThrough) return i;
        }

        return -1;
    }

    void OnCurrentChanged(object? sender, PropertyChangedEventArgs e) {
        // A choice on this page, such as the edition, can change which later pages pass through.
        this.RaisePropertyChanged(nameof(Eyebrow));
        this.RaisePropertyChanged(nameof(FinishLaterVisible));
        this.RaisePropertyChanged(nameof(OpenInBrowserVisible));
        switch (e.PropertyName) {
            case nameof(IWizardStep.NextLabel): this.RaisePropertyChanged(nameof(NextLabel)); break;
            case nameof(IWizardStep.SkipLabel): this.RaisePropertyChanged(nameof(SkipLabel)); break;
            case nameof(IWizardStep.OwnsPrimaryAction) or nameof(IWizardStep.Skippable)
                or nameof(IWizardStep.ShowsOwnPrimary) or nameof(IWizardStep.CanContinue) or nameof(IWizardStep.CanSkip)
                or nameof(IWizardStep.BackVisible):
                RestateActions();
                break;
            case nameof(SignInStepViewModel.TenantPickerVisible)
                or nameof(SignInStepViewModel.TenantChoicePending):
                this.RaisePropertyChanged(nameof(TenantActionsVisible));
                this.RaisePropertyChanged(nameof(FooterVisible));
                this.RaisePropertyChanged(nameof(ConfirmTenantCommand));
                this.RaisePropertyChanged(nameof(CancelTenantCommand));
                break;
        }
    }

    void RestateActions() {
        this.RaisePropertyChanged(nameof(Eyebrow));
        this.RaisePropertyChanged(nameof(CanGoNext));
        this.RaisePropertyChanged(nameof(CanGoSkip));
        this.RaisePropertyChanged(nameof(NextVisible));
        this.RaisePropertyChanged(nameof(SkipVisible));
        this.RaisePropertyChanged(nameof(BackVisible));
        this.RaisePropertyChanged(nameof(FinishLaterVisible));
        this.RaisePropertyChanged(nameof(FooterVisible));
        this.RaisePropertyChanged(nameof(NextIsPrimary));
        this.RaisePropertyChanged(nameof(NextIsSecondary));
        this.RaisePropertyChanged(nameof(OpenInBrowserVisible));
        this.RaisePropertyChanged(nameof(OpenInBrowserCommand));
        this.RaisePropertyChanged(nameof(TenantActionsVisible));
        this.RaisePropertyChanged(nameof(ConfirmTenantCommand));
        this.RaisePropertyChanged(nameof(CancelTenantCommand));
        _restated.OnNext(Unit.Default);
    }

    /// Idempotent — a Done-finish close and the window's own Closing event both route here.
    internal void RequestClose() {
        if (_closed) return;
        _closed = true;
        _advancePending = false;
        this.RaisePropertyChanged(nameof(CanGoNext));
        this.RaisePropertyChanged(nameof(CanGoSkip));
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Jump straight to a step (an expired browser sign-up back to Edition). Goes through the SAME
    /// serialized gate and the same <see cref="IWizardStep.CanLeaveAsync"/> veto as a button
    /// transition. False = refused: a navigation is already in flight, or that step isn't part of
    /// this run (an inapplicable step was filtered out at construction).
    /// </summary>
    internal bool TryGoTo(WizardStepId id) {
        if (_navigating || _closed) return false;

        var target = -1;
        for (var i = 0; i < Steps.Count && target < 0; i++) {
            if (Steps[i].Id == id) target = i;
        }

        if (target < 0) return false;
        if (target == _index) return true; // already there — nothing to transition

        Navigating = true; // claimed here, released by GoToAsync's finally
        _ = GoToAsync(target);

        return true;
    }

    async Task NavigateAsync(WizardNavigation direction) {
        if (_navigating || _closed) return;
        Navigating = true;
        try {
            if (direction == WizardNavigation.Back && PreviousShownIndex() < 0) return;
            if (!await SafeCanLeaveAsync(Current, direction)) return;

            if (direction == WizardNavigation.Next && _index == Steps.Count - 1) {
                RequestClose();
                return;
            }

            var target = direction == WizardNavigation.Back ? PreviousShownIndex() : FirstShownFrom(_index + 1);
            // Clamp, don't trust the arithmetic — a corrupted _index must never throw here.
            await MoveToAsync(Math.Clamp(target, 0, Steps.Count - 1));
        } finally {
            ReleaseGate();
        }
    }

    // The last step is always shown, so the wizard has somewhere to finish.
    int FirstShownFrom(int index) {
        while (index < Steps.Count - 1 && Steps[index].PassThrough) index++;
        return index;
    }

    // The gate is already held by TryGoTo; direction is reported to the leaving step as the move it
    // actually is, so a step that vetoes going forward still vetoes a jump forward.
    async Task GoToAsync(int target) {
        try {
            var direction = target < _index ? WizardNavigation.Back : WizardNavigation.Next;
            if (!await SafeCanLeaveAsync(Current, direction)) return;

            await MoveToAsync(target);
        } finally {
            ReleaseGate();
        }
    }

    // Caller holds the Navigating gate and the leaving step has already released it.
    async Task MoveToAsync(int index) {
        _index = index;
        _visited[index] = true;
        Current = Steps[_index];
        await SafeEnterAsync(Current);
    }

    // A throwing veto must not crash the app — treat it the same as an honest "no".
    async Task<bool> SafeCanLeaveAsync(IWizardStep step, WizardNavigation direction) {
        try {
            return await step.CanLeaveAsync(direction, _shutdownToken);
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: wizard step CanLeaveAsync failed unexpectedly: {ex.Message}");
            return false;
        }
    }

    // Entry side effects are best-effort — a throw here must not undo the transition already made.
    async Task SafeEnterAsync(IWizardStep step) {
        try {
            await step.OnEnterAsync(_shutdownToken);
        } catch (OperationCanceledException) {
            // shutdown mid-entry: nothing to roll back
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: wizard step OnEnterAsync failed unexpectedly: {ex.Message}");
        }
    }
}
