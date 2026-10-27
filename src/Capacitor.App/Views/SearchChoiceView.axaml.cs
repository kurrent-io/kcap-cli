using System.Reactive.Linq;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class SearchChoiceView : UserControl {
    Window? _window;

    public SearchChoiceView() {
        InitializeComponent();
        // A setup pane and the sidebar flyout can share a window without sharing radio groups.
        SearchKeywordOption.GroupName = SearchBuiltinOption.GroupName = SearchOllamaOption.GroupName = $"search-mode-{Guid.NewGuid():N}";
        SearchDefaultOption.GroupName = SearchOtherOption.GroupName = $"search-model-{Guid.NewGuid():N}";
        Loaded += async (_, _) => {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Activated += OnWindowActivated;
            if (DataContext is SearchChoiceViewModel vm) await vm.CheckOllamaCommand.Execute();
        };
        Unloaded += (_, _) => {
            if (_window is not null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
    }

    // Someone who left to install or open Ollama is back once the window is, so they need not click Check again.
    async void OnWindowActivated(object? sender, EventArgs e) {
        if (IsEffectivelyVisible && DataContext is SearchChoiceViewModel { OllamaMissing: true } vm)
            await vm.CheckOllamaCommand.Execute();
    }
}
