using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class SessionSearchViewModel : ReactiveObject, IDisposable {
    public const int MinQueryLength = 3;
    static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    readonly Func<SearchRequest, CancellationToken, Task<SearchFetch>> _search;
    readonly TimeProvider _time;
    readonly IScheduler _scheduler;
    readonly ObservableCollection<SearchHitViewModel> _hits = [];
    readonly CompositeDisposable _disposables = new();
    readonly Subject<string> _queryChanges = new();

    string _query = "";
    SearchMode _tab = SearchMode.Sessions;
    int _highlightIndex;
    SearchListState _listState = SearchListState.Empty;
    string? _listMessage;
    bool _isLoading;
    SessionIdentity? _identity;
    CancellationTokenSource? _inFlight;

    public SessionSearchViewModel(
        Func<SearchRequest, CancellationToken, Task<SearchFetch>> search,
        IObservable<SessionIdentity> identity,
        IObservable<string> accessChanged,
        TimeProvider time,
        IScheduler scheduler,
        TimeSpan? debounce = null) {
        _search = search;
        _time = time;
        _scheduler = scheduler;
        Hits = new ReadOnlyObservableCollection<SearchHitViewModel>(_hits);

        _queryChanges.Throttle(debounce ?? DefaultDebounce, scheduler)
            .Subscribe(_ => RunCurrent())
            .DisposeWith(_disposables);
        identity.Subscribe(OnIdentity).DisposeWith(_disposables);
        accessChanged.Subscribe(_ => RunCurrent()).DisposeWith(_disposables);
    }

    public ReadOnlyObservableCollection<SearchHitViewModel> Hits { get; }

    public string Query {
        get => _query;
        set {
            value ??= "";
            if (_query == value) return;
            this.RaiseAndSetIfChanged(ref _query, value);
            if (Trimmed.Length < MinQueryLength) {
                CancelInFlight();
                Clear();
                SetState(Trimmed.Length == 0 ? SearchListState.Empty : SearchListState.TooShort);
            }
            _queryChanges.OnNext(value);
        }
    }

    public SearchMode Tab {
        get => _tab;
        set {
            if (_tab == value) return;
            this.RaiseAndSetIfChanged(ref _tab, value);
            Clear();
            RunCurrent();
        }
    }

    public int HighlightIndex {
        get => _highlightIndex;
        private set {
            if (!EqualityComparer<int>.Default.Equals(_highlightIndex, value)) this.RaiseAndSetIfChanged(ref _highlightIndex, value);
            for (var i = 0; i < _hits.Count; i++) _hits[i].IsHighlighted = i == value;
        }
    }

    public SearchListState ListState { get => _listState; private set => this.RaiseAndSetIfChanged(ref _listState, value); }
    public string? ListMessage { get => _listMessage; private set => this.RaiseAndSetIfChanged(ref _listMessage, value); }
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    string Trimmed => _query.Trim();

    public void MoveHighlight(int delta) {
        if (_hits.Count == 0) return;
        HighlightIndex = Math.Clamp(_highlightIndex + delta, 0, _hits.Count - 1);
    }

    public void NextTab() => Tab = Tab == SearchMode.Sessions ? SearchMode.Transcripts : SearchMode.Sessions;

    public void PreviousTab() => NextTab();

    public void Reset() {
        CancelInFlight();
        _query = "";
        _tab = SearchMode.Sessions;
        this.RaisePropertyChanged(nameof(Query));
        this.RaisePropertyChanged(nameof(Tab));
        Clear();
        SetState(SearchListState.Empty);
    }

    void OnIdentity(SessionIdentity next) {
        var previous = _identity;
        _identity = next;
        if (previous is null) return;
        if (next.Account != previous.Account) Reset();
        else if (next.Generation != previous.Generation && next.Connected) RunCurrent();
    }

    void RunCurrent() {
        if (Trimmed.Length < MinQueryLength) return;

        CancelInFlight();
        var cts = new CancellationTokenSource();
        _inFlight = cts;
        var request = new SearchRequest(Trimmed, _tab);
        IsLoading = true;
        if (_hits.Count == 0) SetState(SearchListState.Searching);
        _ = FetchAsync(request, cts);
    }

    async Task FetchAsync(SearchRequest request, CancellationTokenSource cts) {
        SearchFetch? fetch;
        try {
            fetch = await _search(request, cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception) {
            fetch = new SearchFetch(ServerReadOutcome.Failed, null);
        }
        _scheduler.Schedule(() => {
            if (!ReferenceEquals(_inFlight, cts) || cts.IsCancellationRequested) return;
            _inFlight = null;
            IsLoading = false;
            Apply(request, fetch);
        });
    }

    void Apply(SearchRequest request, SearchFetch fetch) {
        switch (fetch.Outcome) {
            case ServerReadOutcome.Unauthorized:
                Clear();
                SetState(SearchListState.SignInRequired);
                return;
            case ServerReadOutcome.Ok:
                break;
            default:
                Clear();
                SetState(SearchListState.Failed, "Search failed · try again");
                return;
        }

        var now = _time.GetLocalNow();
        var rows = request.Mode == SearchMode.Sessions
            ? (fetch.Response?.Sessions ?? []).Select(h => SearchHitViewModel.ForSession(h, now))
            : (fetch.Response?.Transcripts ?? []).Select(h => SearchHitViewModel.ForTranscript(h, now));
        _hits.Clear();
        foreach (var row in rows) _hits.Add(row);
        HighlightIndex = 0;

        if (_hits.Count > 0) SetState(SearchListState.Results);
        else SetState(SearchListState.NoResults, request.Mode == SearchMode.Sessions ? "No matching sessions" : "No transcript matches");
    }

    void Clear() {
        _hits.Clear();
        HighlightIndex = 0;
    }

    void CancelInFlight() {
        var cts = _inFlight;
        _inFlight = null;
        IsLoading = false;
        if (cts is null) return;
        cts.Cancel();
    }

    void SetState(SearchListState state, string? message = null) {
        ListState = state;
        ListMessage = state == SearchListState.TooShort ? "Type at least 3 characters" : message;
    }

    public void Dispose() {
        CancelInFlight();
        _disposables.Dispose();
    }
}
