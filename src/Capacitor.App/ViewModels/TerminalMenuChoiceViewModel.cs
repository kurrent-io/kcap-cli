using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One choice on a terminal menu the chat answers. Keys are what the vendor's menu reads to pick it.
public sealed class TerminalMenuChoiceViewModel {
    public TerminalMenuChoiceViewModel(string label, byte[] keys, ReactiveCommand<Unit, Unit> choose) {
        Label  = label;
        Keys   = keys;
        Choose = choose;
    }

    public string Label { get; }
    public byte[] Keys { get; }
    public ReactiveCommand<Unit, Unit> Choose { get; }
}
