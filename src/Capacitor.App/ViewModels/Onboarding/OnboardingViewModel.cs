using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services.Onboarding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// Back/Next/Skip all funnel through Current's CanLeaveAsync veto; Next on the last step finishes.
public sealed class OnboardingViewModel : ReactiveObject {
    readonly CancellationToken _shutdownToken;
    bool _closed;
    bool _navigating;
    bool _advancePending;

    internal bool Closed => _closed;

    public IReadOnlyList<IWizardStep> Steps { get; }

    /// Wizard-first mode builds no tray and no main window, so the outcome consumer's Status/
    /// Attention lines are rendered here. Null in tests that don't need them.
    public WizardLifecycleSurface? Surface { get; }

    int _index;

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

    public string Eyebrow =>
        $"STEP {_index + 1} OF {Steps.Count} · {Current.Eyebrow.ToUpper(CultureInfo.CurrentCulture)}";

    public string NextLabel => Current.NextLabel ?? (_index == Steps.Count - 1 ? "Get started" : "Next");
    public string SkipLabel => Current.SkipLabel;
    public bool NextVisible => !Current.OwnsPrimaryAction;
    public bool SkipVisible => _index < Steps.Count - 1 && Current.Skippable && !Current.OwnsPrimaryAction;
    public bool BackVisible => PreviousShownIndex() >= 0;
    public bool CanGoNext => !Navigating && !_closed && Current.CanContinue;
    public bool CanGoSkip => !Navigating && !_closed && Current.CanSkip && _index < Steps.Count - 1;

    /// The workspace list's own actions share the footer line with Back.
    public bool TenantActionsVisible => Current is SignInStepViewModel { TenantChoicePending: true };

    public System.Windows.Input.ICommand? ConfirmTenantCommand =>
        Current is SignInStepViewModel signIn ? signIn.ConfirmTenantCommand : null;

    public System.Windows.Input.ICommand? CancelTenantCommand =>
        Current is SignInStepViewModel signIn ? signIn.CancelTenantCommand : null;

    /// One filled button per screen. Next is that button on a step with no action of its own, and
    /// once the step's own action has finished. While the action is on screen, Next is secondary.
    public bool NextIsPrimary => !Current.ShowsOwnPrimary;

    public bool NextIsSecondary => Current.ShowsOwnPrimary;

    /// The daemon is enabled beside the steps; its outcome lines land on the page that closes setup.
    public bool ShowLifecycleStatus =>
        Current.Id == WizardStepId.Done && !string.IsNullOrEmpty(Surface?.StatusText);

    public bool ShowLifecycleAttention =>
        Current.Id == WizardStepId.Done && !string.IsNullOrEmpty(Surface?.AttentionText);

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

    /// Fires once per logical close: the Done step's finish, or the window closing.
    public event Action? CloseRequested;

    /// The constructor's own initial-entry call, exposed so a test can await it deterministically.
    internal Task PendingEnterForTesting { get; }

    /// The navigation the current step's own action last started, so a test can await it.
    internal Task PendingAdvanceForTesting { get; private set; } = Task.CompletedTask;

    public OnboardingViewModel(
            IEnumerable<IWizardStep> steps, CancellationToken shutdownToken = default,
            WizardLifecycleSurface? surface = null, WizardStepId? startAt = null,
            IObservable<bool>? canFinishLater = null) {
        _shutdownToken = shutdownToken;
        Surface = surface;
        var applicable = steps.Where(s => s.Applicable).ToList();
        Steps = applicable;
        if (Steps.Count == 0) throw new ArgumentException("at least one applicable step is required", nameof(steps));

        _index = startAt is { } id ? Math.Max(0, applicable.FindIndex(s => s.Id == id)) : 0;
        Current = Steps[_index];

        var currentChanged = this.WhenAnyValue(x => x.Current);
        var idle = this.WhenAnyValue(x => x.Navigating).Select(busy => !busy);
        var canBack = currentChanged.CombineLatest(idle, (_, notBusy) => notBusy && PreviousShownIndex() >= 0);

        BackCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Back), canBack);
        SkipCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Skip),
            this.WhenAnyValue(x => x.CanGoSkip));
        NextCommand = ReactiveCommand.CreateFromTask(() => NavigateAsync(WizardNavigation.Next),
            this.WhenAnyValue(x => x.CanGoNext));
        FinishLaterCommand = ReactiveCommand.Create(RequestClose,
            idle.CombineLatest(canFinishLater ?? Observable.Return(true), (notBusy, allowed) => notBusy && allowed));

        if (surface is not null) {
            surface.WhenAnyValue(x => x.StatusText)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowLifecycleStatus)));
            surface.WhenAnyValue(x => x.AttentionText)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(ShowLifecycleAttention)));
        }

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
        switch (e.PropertyName) {
            case nameof(IWizardStep.NextLabel): this.RaisePropertyChanged(nameof(NextLabel)); break;
            case nameof(IWizardStep.SkipLabel): this.RaisePropertyChanged(nameof(SkipLabel)); break;
            case nameof(IWizardStep.Eyebrow):   this.RaisePropertyChanged(nameof(Eyebrow)); break;
            case nameof(IWizardStep.OwnsPrimaryAction) or nameof(IWizardStep.Skippable)
                or nameof(IWizardStep.ShowsOwnPrimary) or nameof(IWizardStep.CanContinue) or nameof(IWizardStep.CanSkip):
                RestateActions();
                break;
            case nameof(SignInStepViewModel.TenantPickerVisible)
                or nameof(SignInStepViewModel.TenantChoicePending):
                this.RaisePropertyChanged(nameof(TenantActionsVisible));
                this.RaisePropertyChanged(nameof(ConfirmTenantCommand));
                this.RaisePropertyChanged(nameof(CancelTenantCommand));
                break;
        }
    }

    void RestateActions() {
        this.RaisePropertyChanged(nameof(CanGoNext));
        this.RaisePropertyChanged(nameof(CanGoSkip));
        this.RaisePropertyChanged(nameof(NextVisible));
        this.RaisePropertyChanged(nameof(SkipVisible));
        this.RaisePropertyChanged(nameof(BackVisible));
        this.RaisePropertyChanged(nameof(NextIsPrimary));
        this.RaisePropertyChanged(nameof(NextIsSecondary));
        this.RaisePropertyChanged(nameof(ShowLifecycleStatus));
        this.RaisePropertyChanged(nameof(ShowLifecycleAttention));
        this.RaisePropertyChanged(nameof(TenantActionsVisible));
        this.RaisePropertyChanged(nameof(ConfirmTenantCommand));
        this.RaisePropertyChanged(nameof(CancelTenantCommand));
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
    /// Jump straight to a step (the Sign-in step's retarget answer). Goes through the SAME
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
