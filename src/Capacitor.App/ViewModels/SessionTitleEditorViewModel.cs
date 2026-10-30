using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A session header's title with an in-place rename. A failed save keeps the draft so the user can
/// retry; the title itself only ever follows the source, apart from showing an applied rename at once.
public sealed class SessionTitleEditorViewModel : ReactiveObject, IDisposable {
    readonly Func<string, CancellationToken, Task<SessionRenameResult>> _rename;
    readonly IAppNotifier _notifier;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _disposed = new();
    // Command gates go through subjects rather than this.WhenAnyValue, which needs ReactiveUI's
    // global init that only a built app primes.
    readonly BehaviorSubject<bool> _canBegin = new(false);
    readonly BehaviorSubject<bool> _canFinish = new(false);

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string _draft = "";
    public string Draft { get => _draft; set => this.RaiseAndSetIfChanged(ref _draft, value); }

    bool _isEditing;
    public bool IsEditing {
        get => _isEditing;
        private set {
            this.RaiseAndSetIfChanged(ref _isEditing, value);
            Refresh();
        }
    }

    bool _isSaving;
    public bool IsSaving {
        get => _isSaving;
        private set {
            this.RaiseAndSetIfChanged(ref _isSaving, value);
            Refresh();
        }
    }

    bool _canRename;
    public bool CanRename {
        get => _canRename;
        private set {
            this.RaiseAndSetIfChanged(ref _canRename, value);
            Refresh();
        }
    }

    public bool ShowsPencil => CanRename && !IsEditing;
    public bool IsInputEnabled => IsEditing && !IsSaving;

    public ReactiveCommand<Unit, Unit> BeginEditCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public SessionTitleEditorViewModel(
            IObservable<string> title, IObservable<bool> canRename,
            Func<string, CancellationToken, Task<SessionRenameResult>> rename, IAppNotifier notifier) {
        _rename = rename;
        _notifier = notifier;

        title.Subscribe(t => Title = t).DisposeWith(_disposables);
        canRename.DistinctUntilChanged().Subscribe(can => {
            CanRename = can;
            if (!can && IsEditing) Cancel();
        }).DisposeWith(_disposables);

        BeginEditCommand = ReactiveCommand.Create(BeginEdit, _canBegin).DisposeWith(_disposables);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, _canFinish).DisposeWith(_disposables);
        CancelCommand = ReactiveCommand.Create(Cancel, _canFinish).DisposeWith(_disposables);
    }

    void Refresh() {
        this.RaisePropertyChanged(nameof(ShowsPencil));
        this.RaisePropertyChanged(nameof(IsInputEnabled));
        _canBegin.OnNext(CanRename && !IsEditing);
        _canFinish.OnNext(IsEditing && !IsSaving);
    }

    void BeginEdit() {
        if (!CanRename || IsEditing) return;
        Draft = Title;
        IsEditing = true;
    }

    void Cancel() {
        Draft = Title;
        IsEditing = false;
    }

    async Task SaveAsync() {
        if (!IsEditing || IsSaving) return;
        var normalized = RenameWire.NormalizeTitle(Draft);
        if (normalized is null || string.Equals(normalized, Title, StringComparison.Ordinal)) {
            Cancel();
            return;
        }

        IsSaving = true;
        SessionRenameResult result;
        try {
            result = await _rename(normalized, _disposed.Token);
        } catch (OperationCanceledException) when (_disposed.IsCancellationRequested) {
            return;
        } catch (Exception) {
            result = new SessionRenameResult(false, null, null);
        } finally {
            IsSaving = false;
        }

        if (result.Applied) {
            Title = result.Title is { Length: > 0 } shown ? shown : normalized;
            Draft = Title;
            IsEditing = false;
            return;
        }
        _notifier.Notify(FailureCopy(result.Reason));
    }

    internal static string FailureCopy(string? reason) => reason switch {
        SessionRenameReasons.NotOwned => "You can't rename a session you don't own",
        SessionRenameReasons.Refused => "The server kept a newer title",
        SessionRenameReasons.Transport or SessionRenameReasons.Unauthenticated or SessionRenameReasons.ServerError
            => "Couldn't rename the session. Check your connection and try again",
        _ => "Couldn't rename the session",
    };

    public void Dispose() {
        _disposed.Cancel();
        _disposables.Dispose();
        _disposed.Dispose();
    }
}
