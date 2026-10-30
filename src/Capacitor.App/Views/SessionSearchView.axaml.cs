using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The search page drawn over the main area. DataContext is the window's MainWindowViewModel,
/// inherited. Focus never leaves the query box: the tabs and rows are not focusable, and the box
/// takes the page's keys before the window's navigation sees them.
public partial class SessionSearchView : UserControl {
    readonly SerialDisposable _focusRequests = new();

    public SessionSearchView() {
        InitializeComponent();
        SearchQueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        this.GetObservable(IsVisibleProperty).Subscribe(visible => {
            if (visible) Dispatcher.UIThread.Post(() => SearchQueryBox.Focus(), DispatcherPriority.Loaded);
        });
        DataContextChanged += (_, _) => _focusRequests.Disposable = Window?.SearchFocusRequests.Subscribe(_ => FocusAndSelect());
    }

    MainWindowViewModel? Window => DataContext as MainWindowViewModel;
    SessionSearchViewModel? Search => Window?.Search;

    void FocusAndSelect() {
        SearchQueryBox.Focus();
        SearchQueryBox.SelectAll();
    }

    void OnQueryKeyDown(object? sender, KeyEventArgs e) {
        if (Window is not { } window) return;
        var search = window.SearchShowsSignIn ? null : Search;
        switch (e.Key) {
            case Key.Escape:
                window.DismissSearchCommand.Execute().Subscribe();
                break;
            case Key.Up:
                search?.MoveHighlight(-1);
                break;
            case Key.Down:
                search?.MoveHighlight(1);
                break;
            case Key.Enter:
                if (search is not null) _ = search.OpenHighlighted();
                break;
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                search?.PreviousTab();
                break;
            case Key.Tab:
                search?.NextTab();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnSessionsTabClick(object? sender, RoutedEventArgs e) => SelectTab(SearchMode.Sessions);

    void OnTranscriptsTabClick(object? sender, RoutedEventArgs e) => SelectTab(SearchMode.Transcripts);

    void SelectTab(SearchMode tab) {
        if (Search is { } search) search.Tab = tab;
        SearchQueryBox.Focus();
    }

    void OnHitClick(object? sender, RoutedEventArgs e) {
        if (sender is Button { DataContext: SearchHitViewModel hit } && Search is { } search) _ = search.OpenAsync(hit);
    }

    void OnHitPointerEntered(object? sender, PointerEventArgs e) {
        if (sender is not Button { DataContext: SearchHitViewModel hit } || Search is not { } search) return;
        var index = search.Hits.IndexOf(hit);
        if (index >= 0) search.MoveHighlight(index - search.HighlightIndex);
    }

    // Only the strip above the page's panel stands in for the title bar.
    void OnBackgroundPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (ReferenceEquals(e.Source, SearchBackground) && e.GetPosition(this).Y < 20) WindowChrome.BeginDrag(this, e);
    }
}
