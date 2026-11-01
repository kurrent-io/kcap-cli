using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// A hand-off composer's contents as the chips showed them: PermissionMode and CodexPosture are the
/// chip values, not what the launch sent. Error is the failure that handed the draft back.
public sealed record HandoffDraft(
    string Text, IReadOnlyList<StagedAttachment> Attachments, string Vendor, string Model, string? Effort,
    string? PermissionMode, CodexLaunchPosture? CodexPosture, string? Error) {
    /// A later draft for the same source: both prompts and every file stay, and the later chips and error win.
    public HandoffDraft MergedWith(HandoffDraft later) => later with {
        Text = JoinText(Text, later.Text),
        Attachments = [.. Attachments, .. later.Attachments.Where(f => Attachments.All(kept => kept.Id != f.Id))],
    };

    /// Neither prompt is dropped: kept comes first, and the same text is not repeated.
    public static string JoinText(string kept, string returned) =>
        kept.Length == 0 ? returned
        : returned.Length == 0 || kept == returned ? kept
        : $"{kept}\n\n{returned}";
}
