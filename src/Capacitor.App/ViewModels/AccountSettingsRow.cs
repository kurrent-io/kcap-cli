using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// One account as the Accounts tab shows it. A null Id is the daemon's default directory with no
/// registry entry behind it, whose one action is Add.
public sealed record AccountSettingsRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, string? Email, string? Plan, string? ErrorText, AccountUsageSlot Usage, bool IsDefault) {
    public string StateLabel => RecordingStateLabels.Label(Vendor, RecordingState);
    public bool StateIsAttention => RecordingStateLabels.IsAttention(Vendor, RecordingState);
    public bool StateIsHealthy => RecordingStateLabels.IsHealthy(Vendor, RecordingState);
    public string? StateHint => RecordingStateLabels.Hint(Vendor, RecordingState, Directory);
    public string? StateCommand => RecordingStateLabels.Command(Vendor, RecordingState, Directory);
    public bool IsUnregisteredDefault => Id is null;
}
