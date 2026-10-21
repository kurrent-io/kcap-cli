using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Settings' Appearance tab: the UI scale, which the main window's shortcuts can also change while
/// the tab is open.
public sealed class AppearanceSettingsViewModel : ReactiveObject, IDisposable {
    readonly IDisposable _subscription;
    double _scale;

    public AppearanceSettingsViewModel(UiScaleSetting setting) {
        _scale = setting.Current;
        var scale = this.WhenAnyValue(x => x.Scale);
        SmallerCommand = ReactiveCommand.CreateFromTask(() => setting.SetAsync(UiScale.Smaller(Scale)), scale.Select(s => s > UiScale.Min));
        LargerCommand  = ReactiveCommand.CreateFromTask(() => setting.SetAsync(UiScale.Larger(Scale)), scale.Select(s => s < UiScale.Max));
        ResetCommand   = ReactiveCommand.CreateFromTask(() => setting.SetAsync(UiScale.Default), scale.Select(s => s != UiScale.Default));
        _subscription  = setting.Changes.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Scale = s);
    }

    public double Scale {
        get => _scale;
        private set {
            this.RaiseAndSetIfChanged(ref _scale, value);
            this.RaisePropertyChanged(nameof(ScaleLabel));
        }
    }

    public string ScaleLabel => UiScale.Label(Scale);

    public string ShortcutHint { get; } = OperatingSystem.IsMacOS()
        ? "Scales text and layout together. ⌘+ and ⌘− also change it, and ⌘0 resets it."
        : "Scales text and layout together. Ctrl++ and Ctrl+− also change it, and Ctrl+0 resets it.";

    public ReactiveCommand<Unit, Unit> SmallerCommand { get; }
    public ReactiveCommand<Unit, Unit> LargerCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    public void Dispose() {
        _subscription.Dispose();
        SmallerCommand.Dispose();
        LargerCommand.Dispose();
        ResetCommand.Dispose();
    }
}
