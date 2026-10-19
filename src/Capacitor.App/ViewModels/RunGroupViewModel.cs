using System.Reactive;
using Avalonia.Collections;
using Capacitor.App.Views;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class RunGroupViewModel : ReactiveObject, IDisposable {
    readonly AvaloniaList<RunRow> _rows = new();
    readonly bool _shells;
    bool _isExpanded;
    bool _hasTopDivider;

    public RunGroupViewModel(string title, bool shells) {
        Title = title;
        _shells = shells;
        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
    }

    public string Title { get; }
    public LucideIconKind IconKind => ToolCategoryIcons.ForCategory(_shells ? ToolCategory.Command : ToolCategory.Agent);
    public IAvaloniaReadOnlyList<RunRow> Rows => _rows;
    public int Count => _rows.Count;
    public bool HasRows => Count > 0;
    public string AccessibleName => $"{Title}, {Count} running, {(IsExpanded ? "expanded" : "collapsed")}";
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public bool HasTopDivider {
        get => _hasTopDivider;
        internal set => this.RaiseAndSetIfChanged(ref _hasTopDivider, value);
    }

    public bool IsExpanded {
        get => _isExpanded;
        private set {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(AccessibleName));
        }
    }

    internal void Refresh(IEnumerable<RunRow> running) {
        var count = Count;
        StableRows.Sync(_rows, running.Where(row => row.IsShell == _shells));
        if (!HasRows) IsExpanded = false;
        if (count == Count) return;
        this.RaisePropertyChanged(nameof(Count));
        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(AccessibleName));
    }

    public void Dispose() => ToggleCommand.Dispose();
}
