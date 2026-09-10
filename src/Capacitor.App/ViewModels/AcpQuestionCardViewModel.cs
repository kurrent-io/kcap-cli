using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The NEEDS YOU card for an ACP question. A single-select pick submits at once; a multi-select
/// submits only within its selection bounds; a question with no options takes free text.
public sealed class AcpQuestionCardViewModel : PendingCardViewModel {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly CancellationTokenSource _lifetime = new();
    readonly BehaviorSubject<bool> _answered = new(false);
    string _freeText = "";

    public string Prompt { get; }
    public IReadOnlyList<AcpOptionViewModel> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public bool IsMultiSelect { get; }
    public int MinSelections { get; }
    public int MaxSelections { get; }
    public bool ShowsSubmit => !HasOptions || IsMultiSelect;
    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }

    public string FreeText {
        get => _freeText;
        set { this.RaiseAndSetIfChanged(ref _freeText, value); Refresh(); }
    }

    public bool IsAnswered {
        get {
            if (!HasOptions) return !string.IsNullOrWhiteSpace(FreeText);
            var selected = Options.Count(o => o.IsSelected);
            return IsMultiSelect ? selected >= MinSelections && selected <= MaxSelections : selected == 1;
        }
    }

    public AcpQuestionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions) : base(entry) {
        _entry = entry;
        _permissions = permissions;
        var question = entry.AcpQuestion ?? throw new ArgumentException("not an ACP question", nameof(entry));
        Prompt = question.Prompt;
        IsMultiSelect = question.IsMultiSelect;
        var idle = Busy.Select(b => !b);
        Options = question.Options.Select(o => new AcpOptionViewModel(o.OptionId, o.Label, o.Description, PickAsync, idle, Refresh)).ToList();
        MinSelections = Math.Max(1, question.MinSelections ?? 1);
        MaxSelections = IsMultiSelect ? Math.Max(MinSelections, question.MaxSelections ?? Math.Max(1, Options.Count)) : 1;

        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync, _answered.CombineLatest(idle, (a, i) => a && i));
        Disposables.Add(SubmitCommand);
        Disposables.Add(_answered);
        // Dispose() runs Disposables in order: cancel _lifetime BEFORE disposing it, so the
        // token passed to SubmitAsync observes cancellation rather than firing on a disposed CTS.
        Disposables.Add(Disposable.Create(() => { try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } }));
        Disposables.Add(_lifetime);
    }

    Task PickAsync(AcpOptionViewModel option) {
        if (IsMultiSelect) { option.IsSelected = !option.IsSelected; return Task.CompletedTask; }
        foreach (var o in Options) o.IsSelected = ReferenceEquals(o, option);
        return SubmitAsync();
    }

    void Refresh() {
        _answered.OnNext(IsAnswered);
        this.RaisePropertyChanged(nameof(IsAnswered));
    }

    async Task SubmitAsync() {
        if (IsBusy || IsDisposed || !IsAnswered) return;
        IsBusy = true;
        ErrorText = null;
        try {
            var ids = Options.Where(o => o.IsSelected).Select(o => o.OptionId).ToList();
            var text = HasOptions ? null : FreeText.Trim();
            var outcome = await _permissions.AnswerAcpAsync(_entry, new AcpAnswer(ids, text), _lifetime.Token);
            ErrorText = ErrorTextFor(outcome);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: question submit failed unexpectedly: {ex.Message}");
            ErrorText = "Something went wrong — try again";
        } finally {
            IsBusy = false;
        }
    }
}
