using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Capacitor.App.Views.Onboarding;

sealed class ImportTrackAutomationPeer(ImportTrack owner) : ControlAutomationPeer(owner), IRangeValueProvider {
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    protected override string GetClassNameCore() => nameof(ImportTrack);
    protected override string GetNameCore() => $"{base.GetNameCore()}, {owner.Stop switch {
        0 => "skip",
        1 => "only me",
        2 => "shared",
        _ => "mixed",
    }}";
    protected override string GetHelpTextCore() => "Use the arrow keys to choose skip, only me, or shared. Only me and shared upload this history.";
    public bool IsReadOnly => !owner.IsEffectivelyEnabled;
    public double Minimum => 0;
    public double Maximum => 2;
    public double Value => Math.Max(0, owner.Stop);
    public double SmallChange => 1;
    public double LargeChange => 1;
    internal void NotifyStopChanged(int before, int after) =>
        RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)Math.Max(0, before), (double)Math.Max(0, after));
    public void SetValue(double value) {
        if (IsReadOnly) return;
        if (!double.IsFinite(value) || value < Minimum || value > Maximum)
            throw new ArgumentOutOfRangeException(nameof(value));
        owner.Stop = (int)Math.Round(value);
    }
}
