using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One choice on the usage-limit question. The index is the digit the vendor's menu shows.
public sealed class UsageLimitChoiceViewModel {
    public UsageLimitChoiceViewModel(int index, string label, ReactiveCommand<Unit, Unit> choose) {
        Index  = index;
        Label  = label;
        Choose = choose;
    }

    public int Index { get; }
    public string Label { get; }
    public ReactiveCommand<Unit, Unit> Choose { get; }
}
