using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// A hand-off composer's contents as the chips showed them: PermissionMode and CodexPosture are the
/// chip values, not what the launch sent. Error is the failure that handed the draft back.
public sealed record HandoffDraft(
    string Text, IReadOnlyList<StagedAttachment> Attachments, string Vendor, string Model, string? Effort,
    string? PermissionMode, CodexLaunchPosture? CodexPosture, string? Error);
