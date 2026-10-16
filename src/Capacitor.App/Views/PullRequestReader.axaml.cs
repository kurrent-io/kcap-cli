using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class PullRequestReader : UserControl {
    PullRequestContextViewModel? _model;
    bool _restoring;
    long _restoreVersion;
    public PullRequestReader() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            if (_model is not null) {
                _model.PropertyChanged -= ModelChanged;
                ((INotifyCollectionChanged)_model.Rows).CollectionChanged -= RowsChanged;
            }
            _model = DataContext as PullRequestContextViewModel;
            if (_model is not null) {
                _model.PropertyChanged += ModelChanged;
                ((INotifyCollectionChanged)_model.Rows).CollectionChanged += RowsChanged;
            }
            RestorePosition();
        };
        ReaderScroll.ScrollChanged += (_, _) => {
            if (!_restoring && _model is { ShowReaderContent: true, ShowsLoading: false }) _model.ScrollOffset = ReaderScroll.Offset.Y;
        };
    }
    void ModelChanged(object? sender, PropertyChangedEventArgs change) {
        if (change.PropertyName is nameof(PullRequestContextViewModel.Section) or nameof(PullRequestContextViewModel.ShowReaderContent)
            or nameof(PullRequestContextViewModel.ShowsLoading)) RestorePosition();
    }
    void RowsChanged(object? sender, NotifyCollectionChangedEventArgs change) => RestorePosition();
    void RestorePosition() {
        var version = ++_restoreVersion;
        _restoring = false;
        var model = _model;
        if (model is null || !model.ShowReaderContent) return;
        // A restored tab may be empty until its first page arrives. Layout must not replace
        // the saved offset with the temporary, clamped position of its loading state.
        _restoring = true;
        var offset = model.ScrollOffset;
        var subject = model.Selected?.Subject;
        var section = model.Section;
        Dispatcher.UIThread.Post(() => {
            if (version != _restoreVersion) return;
            if (_model != model || !model.ShowReaderContent || model.Selected?.Subject != subject || model.Section != section) { _restoring = false; return; }
            ReaderScroll.UpdateLayout();
            ReaderScroll.Offset = new Vector(0, offset);
            _restoring = model.ShowsLoading;
        }, DispatcherPriority.Loaded);
    }
}
