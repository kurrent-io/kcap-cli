using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// A group label in the Settings sidebar. It sits among the tabs so the sidebar lays it out in
/// order, but it is styled disabled and unfocusable so it can never be selected, and a screen
/// reader hears it as a heading instead of an unnamed, unavailable tab.
public sealed class SettingsSectionHeader : TabItem {
    protected override Type StyleKeyOverride => typeof(TabItem);

    protected override AutomationPeer OnCreateAutomationPeer() => new SettingsSectionHeaderAutomationPeer(this);
}
