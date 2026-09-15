using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// <summary>
/// One report, from the form to the lane. A pressed Send binds the report's content — category,
/// composed message, id — into a snapshot; an unchanged retry re-sends that snapshot, and any edit
/// releases it and mints a new id, so a retry can never quietly carry an older text or a newer
/// trailer under an id the server may already have accepted.
/// </summary>
public sealed class FeedbackViewModel : ReactiveObject, IDisposable {
    const string ClientMarker = "Desktop ";

    readonly IFeedbackApi _api;
    readonly string _os;
    readonly Action? _signIn;
    readonly CancellationTokenSource _lifetime;
    readonly CompositeDisposable _subscriptions = new();

    string _liveTrailer = "";
    FeedbackSubmission? _bound;
    string? _boundTrailer;
    Guid _id = Guid.NewGuid();
    FeedbackCategory _category;
    string _message = "";
    bool _isBusy;
    bool _isSent;
    string? _reporterEmail;
    string? _outcome;
    bool _signInOffered;
    bool _disposed;

    public FeedbackViewModel(IFeedbackApi api, FeedbackCategory initial, IObservable<string> trailer,
            string osDescription, Action? signIn, CancellationToken appLifetime = default) {
        _api      = api;
        _os       = osDescription;
        _signIn   = signIn;
        _category = initial;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);

        trailer.Subscribe(t => { _liveTrailer = t; RaiseDerived(); }).DisposeWith(_subscriptions);

        SendCommand        = ReactiveCommand.CreateFromTask(SendAsync, this.WhenAnyValue(x => x.CanSend));
        SendAnotherCommand = ReactiveCommand.Create(() => StartNewReport(_category));
        SignInCommand      = ReactiveCommand.Create(() => { _signIn?.Invoke(); }, this.WhenAnyValue(x => x.SignInOffered));
    }

    public FeedbackCategory Category {
        get => _category;
        set {
            // A two-way chip binding can fire mid-send; the frozen snapshot is not touched, and the
            // re-announce snaps the control back to the category actually being sent.
            if (IsBusy) { this.RaisePropertyChanged(nameof(Category)); return; }
            if (_category == value) return;
            Release();
            _category = value;
            this.RaisePropertyChanged(nameof(Category));
            RaiseDerived();
        }
    }

    public string Message {
        get => _message;
        set {
            if (_message == value) return;
            Release();
            _message = value;
            this.RaisePropertyChanged(nameof(Message));
            RaiseDerived();
        }
    }

    public string Hint => BuildHint();

    public bool CanSend =>
        !IsBusy && !IsSent && _message.Trim().Length > 0 &&
        FeedbackMessageComposer.Remaining(_message, EffectiveTrailer) >= 0;

    public bool IsBusy {
        get => _isBusy;
        private set {
            if (_isBusy == value) return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            this.RaisePropertyChanged(nameof(CanSend));
        }
    }

    public bool IsSent {
        get => _isSent;
        private set {
            if (_isSent == value) return;
            this.RaiseAndSetIfChanged(ref _isSent, value);
            this.RaisePropertyChanged(nameof(CanSend));
        }
    }

    public string? ReporterEmail { get => _reporterEmail; private set => this.RaiseAndSetIfChanged(ref _reporterEmail, value); }
    public string? Outcome { get => _outcome; private set => this.RaiseAndSetIfChanged(ref _outcome, value); }
    public bool SignInOffered { get => _signInOffered; private set => this.RaiseAndSetIfChanged(ref _signInOffered, value); }
    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> SendAnotherCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    internal Guid CurrentId => _id;

    /// <summary>A second entry-point click. Ignored mid-send; a sent window starts over.</summary>
    public void Reopen(FeedbackCategory category) {
        if (IsBusy) return;
        if (IsSent) { StartNewReport(category); return; }
        Category = category;
    }

    string EffectiveTrailer => _boundTrailer ?? _liveTrailer;

    /// <summary>The hint discloses the trailer it is sliced from, so the two can never disagree.</summary>
    string BuildHint() {
        var trailer   = EffectiveTrailer;
        var remaining = FeedbackMessageComposer.Remaining(_message, trailer);
        var marker    = trailer.IndexOf(ClientMarker, StringComparison.Ordinal);
        var client    = marker < 0 ? trailer : trailer[(marker + ClientMarker.Length)..];
        return $"Attached automatically: desktop {client} · {_os} · {remaining} characters left";
    }

    void RaiseDerived() {
        this.RaisePropertyChanged(nameof(Hint));
        this.RaisePropertyChanged(nameof(CanSend));
    }

    void Release() {
        if (_bound is null) return;
        _bound = null;
        _boundTrailer = null;
        _id = Guid.NewGuid();
        Outcome = null;
        SignInOffered = false;
    }

    void StartNewReport(FeedbackCategory category) {
        _bound = null;
        _boundTrailer = null;
        _id = Guid.NewGuid();
        IsSent = false;
        ReporterEmail = null;
        Outcome = null;
        SignInOffered = false;
        _category = category; this.RaisePropertyChanged(nameof(Category));
        _message = "";        this.RaisePropertyChanged(nameof(Message));
        RaiseDerived();
    }

    async Task SendAsync() {
        var submission = _bound;
        if (submission is null) {
            _boundTrailer = _liveTrailer;
            submission = _bound = new FeedbackSubmission(
                _category, FeedbackMessageComposer.Compose(_message, _boundTrailer), _id, FeedbackSource.Desktop);
            RaiseDerived();
        }

        IsBusy = true;
        Outcome = null;
        SignInOffered = false;
        try {
            var result = await _api.SubmitAsync(submission, _lifetime.Token);
            if (result is FeedbackResult.Sent(var email)) {
                ReporterEmail = email;
                IsSent = true;
                _bound = null;
                _boundTrailer = null;
                _id = Guid.NewGuid();
                RaiseDerived();
            } else {
                Outcome = FeedbackResultMessages.ForRefusal(result);
            }
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            // The app is quitting; nothing to show.
        } catch (CapacitorApiException ex) when (ex.Status == 401) {
            Outcome = HomeViewModel.SignInExpiredNotice;
            SignInOffered = _signIn is not null;
        } catch (Exception ex) {
            Outcome = $"Couldn't send the report: {ex.Message}";
        } finally {
            IsBusy = false;
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _subscriptions.Dispose();
    }
}
