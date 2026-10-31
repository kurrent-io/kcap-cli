using Avalonia.Automation;
using Avalonia.Automation.Peers;

namespace Capacitor.App.Views;

/// Exposes a Settings sidebar group label as heading text. It offers no selection pattern, and it
/// reports itself enabled: the disabled state only keeps the tab strip from selecting it.
public sealed class SettingsSectionHeaderAutomationPeer(SettingsSectionHeader owner) : ControlAutomationPeer(owner) {
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    // TabItem overrides the default control type to TabItem, which would win over the core type.
    protected override AutomationControlType GetControlTypeOverrideCore() => AutomationControlType.Text;

    protected override string? GetNameCore() =>
        base.GetNameCore() is { Length: > 0 } name ? name : ((SettingsSectionHeader)Owner).Header as string;

    protected override int GetHeadingLevelCore() =>
        AutomationProperties.GetHeadingLevel(Owner) is > 0 and var level ? level : 2;

    protected override bool IsEnabledCore() => true;

    // The header text is the name; its text block would only say it again.
    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
}
