using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace Capacitor.App.Views;

/// An icon supplements a readable label, so an unnamed one stays out of the tree a screen reader
/// walks. An icon given a name carries meaning of its own, such as a row's state, and is exposed
/// as an image with that name; the platform's default peer would hide it whatever its name.
public sealed class AppIconAutomationPeer(AppIcon owner) : ControlAutomationPeer(owner) {
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        IsNamed ? AutomationControlType.Image : AutomationControlType.None;

    protected override bool IsContentElementCore() => IsNamed;

    protected override bool IsControlElementCore() => IsNamed;

    // The glyph's drawing parts mean nothing on their own.
    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;

    bool IsNamed => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(Owner));
}
